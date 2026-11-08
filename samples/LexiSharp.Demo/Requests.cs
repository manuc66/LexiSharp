namespace LexiSharp.Demo;

public sealed record LearnRequest(string Query, string DocumentId, string? SessionId = null, bool Global = false);
public sealed record ReinforceRequest(string Text, string Category);
