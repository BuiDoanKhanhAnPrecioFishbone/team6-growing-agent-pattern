// S4 · Valuation — standalone agent service. All wiring is in the shared host.
await AIAssistant.AgentHost.Host.Run(args, new AIAssistant.Agents.ValuationAgent(), port: 5304, blockKey: "valuation");
