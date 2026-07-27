// S3 · Financials — standalone agent service. All wiring is in the shared host.
await AIAssistant.AgentHost.Host.Run(args, new AIAssistant.Agents.FinancialsAgent(), port: 5303, blockKey: "financials");
