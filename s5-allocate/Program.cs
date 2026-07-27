// S5 · Allocate — standalone agent service. All wiring is in the shared host.
await AIAssistant.AgentHost.Host.Run(args, new AIAssistant.Agents.AllocateAgent(), port: 5305, blockKey: "allocation");
