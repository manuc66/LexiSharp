namespace LexiSharp.Tests.Conformance;

/// <summary>
/// Which optional query features an engine under conformance supports. Unsupported features are
/// skipped by the suite (they are not silently treated as supported).
/// </summary>
/// <param name="Phrases">Quoted segments gate on consecutive positions.</param>
/// <param name="Expansions">Prefix/fuzzy operators (<c>term*</c>/<c>term~N</c>) are honored.</param>
public sealed record EngineCapabilities(bool Phrases = false, bool Expansions = false);
