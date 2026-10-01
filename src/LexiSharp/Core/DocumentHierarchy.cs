namespace LexiSharp.Core;

/// <summary>
/// A validated parent/child forest over searchable document ids — the « hierarchical metadata »
/// seam. Declare the tree your chunks belong to (a chunk under a section, a section under a
/// book) once; <see cref="GetAncestors"/> walks from a leaf toward the overview,
/// <see cref="GetDescendants"/> from an overview toward the precise nodes. The library neither
/// chunks documents nor imposes a shape: the hierarchy is app-declared over the ids the caller
/// already indexes.
/// </summary>
/// <remarks>
/// A node is any id that appears as a child or as a parent. A node has at most one parent
/// (a tree, hence a forest); several roots are allowed. Construction validates the whole
/// structure and throws rather than accepting a broken one: no self-parenting, no node with
/// two parents, no cycle — each is named when found.
/// <para>
/// Order is deterministic: <see cref="GetAncestors"/> runs immediate-parent first and ends at
/// the root; <see cref="GetDescendants"/> is breadth-first in declared child order. An id the
/// hierarchy does not know behaves like a root: no ancestors, no descendants, depth zero.
/// </para>
/// </remarks>
public sealed class DocumentHierarchy
{
    private readonly IReadOnlyDictionary<string, string> _parentOf;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _childrenOf;
    private readonly IReadOnlyList<string> _nodes;
    private readonly IReadOnlyList<string> _roots;

    private DocumentHierarchy(
        IReadOnlyDictionary<string, string> parentOf,
        IReadOnlyDictionary<string, IReadOnlyList<string>> childrenOf,
        IReadOnlyList<string> nodes,
        IReadOnlyList<string> roots)
    {
        _parentOf = parentOf;
        _childrenOf = childrenOf;
        _nodes = nodes;
        _roots = roots;
    }

    /// <summary>Builds the hierarchy from a child → parent map. Every value must be a node.</summary>
    /// <param name="childToParent">
    /// Child id → its parent id. Blank ids are rejected; so are self-parenting (a child that is
    /// its own parent) and any cycle in the parent chains.
    /// </param>
    /// <exception cref="ArgumentException">A blank id, a self-parenting entry, or a cycle.</exception>
    public static DocumentHierarchy FromChildToParent(IReadOnlyDictionary<string, string> childToParent)
    {
        ArgumentNullException.ThrowIfNull(childToParent);

        var parentOf = new Dictionary<string, string>(childToParent.Count, StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nodes = new List<string>(childToParent.Count);

        foreach ((string child, string parent) in childToParent)
        {
            RequireId(child);
            RequireId(parent);

            if (string.Equals(child, parent, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Document '{child}' cannot be its own parent.", nameof(childToParent));

            parentOf.Add(child, parent);
            AddNode(nodes, child);
            AddNode(nodes, parent);

            if (!childrenOf.TryGetValue(parent, out var children))
                childrenOf[parent] = children = [];

            if (children.Contains(child, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"Document '{child}' is declared under parent '{parent}' more than once.", nameof(childToParent));

            children.Add(child);
        }

        return Finish(parentOf, childrenOf, nodes);
    }

    /// <summary>Builds the hierarchy from a parent → ordered children map. Every child appears once.</summary>
    /// <param name="parentToChildren">
    /// Parent id → its children, in the order <see cref="GetDescendants"/> should visit them.
    /// Blank ids are rejected; so is a child listed under two parents, a parent that is its own
    /// child, and any cycle.
    /// </param>
    /// <exception cref="ArgumentException">A blank id, a duplicated child, or a cycle.</exception>
    public static DocumentHierarchy FromParentToChildren(IReadOnlyDictionary<string, IReadOnlyList<string>> parentToChildren)
    {
        ArgumentNullException.ThrowIfNull(parentToChildren);

        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nodes = new List<string>(parentToChildren.Count);

        foreach ((string parent, IReadOnlyList<string> children) in parentToChildren)
        {
            RequireId(parent);
            ArgumentNullException.ThrowIfNull(children);

            AddNode(nodes, parent);

            if (!childrenOf.TryGetValue(parent, out var placed))
                childrenOf[parent] = placed = [];

            foreach (string child in children)
            {
                RequireId(child);

                if (string.Equals(child, parent, StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"Document '{child}' cannot be its own parent.", nameof(parentToChildren));

                if (parentOf.ContainsKey(child))
                {
                    throw new ArgumentException(
                        $"Document '{child}' is listed under more than one parent " +
                        $"('{parentOf[child]}' and '{parent}').", nameof(parentToChildren));
                }

                if (placed.Contains(child, StringComparer.Ordinal))
                    throw new ArgumentException(
                        $"Document '{child}' is listed twice under parent '{parent}'.", nameof(parentToChildren));

                AddNode(nodes, child);
                parentOf[child] = parent;
                placed.Add(child);
            }
        }

        return Finish(parentOf, childrenOf, nodes);
    }

    /// <summary>Every id the hierarchy knows, in declaration order.</summary>
    public IReadOnlyCollection<string> Nodes => _nodes;

    /// <summary>The root ids — nodes that are never a child — in declaration order.</summary>
    public IReadOnlyCollection<string> Roots => _roots;

    /// <summary>Total number of declared nodes.</summary>
    public int Count => _nodes.Count;

    /// <summary>True when the hierarchy knows the id.</summary>
    public bool Contains(string documentId) =>
        !string.IsNullOrEmpty(documentId) && _nodes.Contains(documentId, StringComparer.Ordinal);

    /// <summary>Whether the node is a root: a node that is never a child.</summary>
    public bool IsRoot(string documentId) =>
        Contains(documentId) && _roots.Contains(documentId, StringComparer.Ordinal);

    /// <summary>Whether the node is a leaf: a known node with no declared children.</summary>
    public bool IsLeaf(string documentId) =>
        Contains(documentId)
        && (!_childrenOf.TryGetValue(documentId, out var children) || children.Count == 0);

    /// <summary>Reads the node's parent; false for roots and unknown ids.</summary>
    public bool TryGetParent(string documentId, out string parentId) =>
        _parentOf.TryGetValue(documentId, out parentId!);

    /// <summary>
    /// The node's depth: the number of ancestors between it and the root. 0 for roots and
    /// unknown ids.
    /// </summary>
    public int Depth(string documentId) => GetAncestors(documentId).Count;

    /// <summary>
    /// The chain of ancestor ids from the node upward: immediate parent first, ending at the
    /// root. Empty for roots and unknown ids.
    /// </summary>
    public IReadOnlyList<string> GetAncestors(string documentId)
    {
        if (!_parentOf.TryGetValue(documentId, out var current))
            return Array.Empty<string>();

        var ancestors = new List<string>();

        while (current is not null)
        {
            ancestors.Add(current);

            if (!_parentOf.TryGetValue(current, out var parent))
                break;

            current = parent;
        }

        return ancestors;
    }

    /// <summary>
    /// Every descendant of the node, breadth-first in declared child order: children first,
    /// then grandchildren, ... Empty for leaves and unknown ids.
    /// </summary>
    public IReadOnlyList<string> GetDescendants(string documentId)
    {
        if (!_childrenOf.TryGetValue(documentId, out var children))
            return Array.Empty<string>();

        var descendants = new List<string>();
        var frontier = new Queue<string>(children);

        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            descendants.Add(current);

            if (_childrenOf.TryGetValue(current, out var next))
            {
                foreach (string child in next)
                    frontier.Enqueue(child);
            }
        }

        return descendants;
    }

    private static void RequireId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Document ids in a hierarchy must be non-blank.");
    }

    private static void AddNode(List<string> nodes, string id)
    {
        if (!nodes.Contains(id, StringComparer.Ordinal))
            nodes.Add(id);
    }

    /// <summary>Validates the parent chains for cycles, then freezes the structure.</summary>
    private static DocumentHierarchy Finish(
        IReadOnlyDictionary<string, string> parentOf,
        IReadOnlyDictionary<string, List<string>> childrenOf,
        List<string> nodes)
    {
        DetectCycles(parentOf);

        var immutableChildren = new Dictionary<string, IReadOnlyList<string>>(childrenOf.Count, StringComparer.Ordinal);

        foreach ((string parent, List<string> children) in childrenOf)
            immutableChildren[parent] = children;

        var roots = new List<string>();

        foreach (string node in nodes)
        {
            if (!parentOf.ContainsKey(node))
                roots.Add(node);
        }

        return new DocumentHierarchy(parentOf, immutableChildren, nodes, roots);
    }

    /// <summary>
    /// Walks every node's parent chain up and throws when a chain revisits a node — the only
    /// shape a tree forbids, and the only one a node-with-one-parent map can still encode. Each
    /// chain is validated once: an ancestor whose chain was already walked is known to be clean.
    /// </summary>
    private static void DetectCycles(IReadOnlyDictionary<string, string> parentOf)
    {
        var clean = new HashSet<string>(StringComparer.Ordinal);

        foreach (string start in parentOf.Keys)
        {
            if (clean.Contains(start))
                continue;

            var chain = new List<string> { start };

            while (parentOf.TryGetValue(chain[^1], out var parent))
            {
                if (chain.Contains(parent, StringComparer.Ordinal))
                    throw new ArgumentException($"The hierarchy contains a cycle through '{parent}'.");

                if (clean.Contains(parent))
                    break;

                chain.Add(parent);
            }

            foreach (string node in chain)
                clean.Add(node);
        }
    }
}