namespace VisioGenerator;

/// <summary>One horizontal band of the riser (Roof, L8 ... B1).</summary>
public sealed class FloorSpec
{
    public string Name { get; set; } = "";
}

/// <summary>A vertical riser / trunk that runs through the building.</summary>
public sealed class SystemSpec
{
    public string Id { get; set; } = "";
    public string Short { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#2F5597";
    public bool Dashed { get; set; }
}

/// <summary>Equipment placed on a floor (chiller, AHU, pump, controller ...).</summary>
public sealed class NodeSpec
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "equipment";
    public int Floor { get; set; }
    /// <summary>Optional explicit riser membership when no link already implies it.</summary>
    public string? System { get; set; }
}

/// <summary>A connection between two nodes over a given medium (chws, chwr, net ...).</summary>
public sealed class LinkSpec
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Medium { get; set; } = "";
}

/// <summary>The whole drawing request, normally produced by the Selection Navigator backend.</summary>
public sealed class RiserSpec
{
    public string Title { get; set; } = "Riser Diagram";
    public string Subtitle { get; set; } = "";
    public string Project { get; set; } = "";
    public List<FloorSpec> Floors { get; set; } = new();
    public List<SystemSpec> Systems { get; set; } = new();
    public List<NodeSpec> Nodes { get; set; } = new();
    public List<LinkSpec> Links { get; set; } = new();
}
