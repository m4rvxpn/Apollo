from CommandBase import *
import json


class MakeTokenArguments(TaskArguments):

    def __init__(self, command_line):
        super().__init__(command_line)
        self.args = {
            "credential": CommandParameter(name="Credential", type=ParameterType.Credential_JSON)
        }

    async def parse_arguments(self):
        self.load_args_from_json_string(self.command_line)
        if self.args["credential"] is None:
            raise Exception("Require a credential to use make_token with.")


class MakeTokenCommand(CommandBase):
    cmd = "make_token"
    needs_admin = False
    help_cmd = "make_token (modal popup)"
    description = "Creates a new logon session and applies it to the agent. Modal popup for options. Credentials must be populated in the credential store."
    version = 1
    is_exit = False
    is_file_browse = False
    is_process_list = False
    is_download_file = False
    is_upload_file = False
    is_remove_file = False
    author = "@djhohnstein"
    argument_class = MakeTokenArguments
    attackmapping = []

    async def create_tasking(self, task: MythicTask) -> MythicTask:
        return task

    async def process_response(self, response: AgentResponse):
        pass