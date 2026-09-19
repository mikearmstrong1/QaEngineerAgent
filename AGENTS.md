# Project guidance

- Preserve the terminal and CLI workflows. Web interfaces enhance the system and must call the same application contracts without removing, renaming, or weakening existing commands.
- Offload bounded drafts, test proposals, and reviews to the local Qwen endpoint at `http://192.168.1.15:1234/v1/` whenever practical. Source files may be sent to that local endpoint. Independently review all output and run verification locally.
- Keep Jira, OpenAI, API, and storage credentials out of browser responses, source control, logs, and saved evidence.
