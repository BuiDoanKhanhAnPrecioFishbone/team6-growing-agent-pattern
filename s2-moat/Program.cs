// S2 · Moat — standalone agent service. All wiring is in the shared host.
await AIAssistant.AgentHost.Host.Run(args, new AIAssistant.Agents.MoatAgent(), port: 5302, blockKey: "moat");
