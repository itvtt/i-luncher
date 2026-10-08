# Global agent instructions

Work only on the user's latest request and the linked project plan. Before changing files, read the latest plan and inspect the relevant current code and existing changes.

Work in this single session. Do not create coordinator, reviewer, or implementer agents, or repeatedly ask another agent to supervise your work.

Complete one concrete unit of the requested work, then run a meaningful verification for that change. Preserve working behavior. Do not repeat completed work, recreate an existing implementation, or add unrelated improvements. If the request is already complete, explain what confirms that and stop.

Treat questions as questions: answer them without changing files unless the user asks for implementation. Never use a terminal-input journal or archived prompts to infer new work. Follow the user's latest correction when it changes earlier direction.