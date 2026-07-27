// S6 · Monitor — standalone agent service. All wiring is in the shared host.
await AIAssistant.AgentHost.Host.Run(args, new AIAssistant.Agents.MonitorAgent(), port: 5306, blockKey: "monitoring");
