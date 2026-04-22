using System;
using System.Collections.Generic;

namespace LocalPilot.Models
{
    public enum NexusNodeType
    {
        Component,    // Angular/React Component
        FrontendService, // TS Service
        ApiEndpoint,  // The bridge (Virtual Node)
        Controller,   // .NET Controller
        BackendService, // .NET Service
        DataModel     // DTO, Entity, Interface
    }

    public enum NexusEdgeType
    {
        Calls,        // Service calls Endpoint
        MapsTo,       // Endpoint maps to Controller
        DependsOn,    // Component depends on Service
        Implements,   // Service implements Interface
        References    // General reference
    }

    public class NexusNode
    {
        public string Id { get; set; } // Usually the FullPath or a Route template
        public string Name { get; set; }
        public NexusNodeType Type { get; set; }
        public string Language { get; set; } // cs, ts, tsx, html
        public string FilePath { get; set; }
        public int Line { get; set; }
        
        // Metadata for specific types (e.g., HTTP Verb for ApiEndpoint)
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        public override bool Equals(object obj) => obj is NexusNode other && Id == other.Id;
        public override int GetHashCode() => Id?.GetHashCode() ?? 0;
    }

    public class NexusEdge
    {
        public string FromId { get; set; }
        public string ToId { get; set; }
        public NexusEdgeType Type { get; set; }
        public string Description { get; set; }

        public override bool Equals(object obj) => 
            obj is NexusEdge other && FromId == other.FromId && ToId == other.ToId && Type == other.Type;
        
        public override int GetHashCode() => 
            (FromId?.GetHashCode() ?? 0) ^ (ToId?.GetHashCode() ?? 0) ^ Type.GetHashCode();
    }

    public class NexusGraph
    {
        public List<NexusNode> Nodes { get; set; } = new List<NexusNode>();
        public List<NexusEdge> Edges { get; set; } = new List<NexusEdge>();
        public DateTime LastUpdated { get; set; }
    }
}
