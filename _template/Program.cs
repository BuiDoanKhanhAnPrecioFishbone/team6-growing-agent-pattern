// A growing agent, in one line. The loop, memory, reward-contract and HTTP surface (/,/run,/lessons)
// all come from the shared host. Copy this folder to agents/sN, rename the agent + port + blockKey,
// and implement the three methods in TemplateAgent.cs.
await AIAssistant.AgentHost.Host.Run(args, new AIAssistant.STemplate.TemplateAgent(), port: 5399, blockKey: "yourBlock");
