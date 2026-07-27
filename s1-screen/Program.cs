// S1 · Screen — standalone agent service. All wiring is in the shared host.
await AIAssistant.AgentHost.Host.Run(args, new AIAssistant.Agents.ScreenAgent(), port: 5301, blockKey: "screen");
