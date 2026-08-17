namespace OpenSage.Data.Ini;

/// <summary>
/// A ChildObject/ObjectReskin block whose parent object was not yet defined
/// when the block was encountered. The raw block source is captured so the
/// block can be re-parsed once the parent exists.
/// </summary>
internal sealed class PendingChildObject
{
    public string Name { get; }
    public string ParentName { get; }
    public bool IsReskin { get; }

    /// <summary>
    /// The raw block body, including the terminating 'End' line.
    /// </summary>
    public string BlockSource { get; }

    public string FilePath { get; }
    public int StartLine { get; }
    public string Directory { get; }

    public PendingChildObject(
        string name,
        string parentName,
        bool isReskin,
        string blockSource,
        string filePath,
        int startLine,
        string directory)
    {
        Name = name;
        ParentName = parentName;
        IsReskin = isReskin;
        BlockSource = blockSource;
        FilePath = filePath;
        StartLine = startLine;
        Directory = directory;
    }
}
