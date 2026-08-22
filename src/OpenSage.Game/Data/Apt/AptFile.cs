using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenSage.Data.Apt.Characters;
using OpenSage.FileFormats;
using OpenSage.IO;

namespace OpenSage.Data.Apt;

public sealed class AptFile
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public FileSystem FileSystem { get; }
    public ConstantData Constants { get; }
    public string MovieName { get; }

    public Movie Movie { get; private set; }
    public ImageMap ImageMap { get; private set; }
    public Dictionary<uint, Geometry> GeometryMap { get; private set; }

    internal bool IsEmpty = true;


    private AptFile(ConstantData constants, FileSystem filesystem, string name)
    {
        Constants = constants;
        FileSystem = filesystem;
        MovieName = name;
    }

    private void Parse(BinaryReader reader, string parentDirectory)
    {
        //jump to the entry offset
        var entryOffset = Constants.AptDataEntryOffset;
        reader.BaseStream.Seek(entryOffset, SeekOrigin.Begin);

        //proceed loading the characters
        Movie = (Movie)Character.Create(reader, this);

        //set first character to itself
        Movie.Characters[0] = Movie;

        //load the corresponding image map, which is optional: a movie that draws no textured
        //geometry ships without one (Age of the Ring's SkyrimMenu.apt, imported by its MainMenu,
        //has no .dat anywhere in the installation).
        var datPath = Path.Combine(parentDirectory, MovieName + ".dat");
        var datEntry = FileSystem.GetFile(datPath);
        if (datEntry != null)
        {
            ImageMap = ImageMap.FromFileSystemEntry(datEntry);
        }
        else
        {
            Logger.Info($"No image map for apt file '{MovieName}'; looked for {datPath}");
            ImageMap = new ImageMap();
        }

        //resolve geometries
        GeometryMap = new Dictionary<uint, Geometry>();
        foreach (Shape shape in Movie.Characters.FindAll((x) => x is Shape))
        {
            var ruPath = Path.Combine(parentDirectory, MovieName + "_geometry", +shape.Geometry + ".ru");
            var shapeEntry = FileSystem.GetFile(ruPath);
            if (shapeEntry == null)
            {
                throw new FileNotFoundException($"Cannot find geometry for apt file '{MovieName}'", ruPath);
            }
            var shapeGeometry = Geometry.FromFileSystemEntry(this, shapeEntry);
            GeometryMap[shape.Geometry] = shapeGeometry;
        }

        var importDict = new Dictionary<string, AptFile>();

        //resolve imports
        foreach (var import in Movie.Imports)
        {
            //open the apt file where our character is located
            AptFile importApt;

            if (importDict.ContainsKey(import.Movie))
            {
                importApt = importDict[import.Movie];
            }
            else
            {
                var importPath = Path.Combine(parentDirectory, Path.ChangeExtension(import.Movie, ".apt"));
                var importEntry = FileSystem.GetFile(importPath);
                if (importEntry == null)
                {
                    throw new FileNotFoundException("Cannot find imported file", importPath);
                }
                importApt = AptFile.FromFileSystemEntry(importEntry);
                importDict[import.Movie] = importApt;
            }

            //get the export from that apt and proceed
            var export = importApt.Movie.Exports.Find(x => x.Name == import.Name);
            if (export == null)
            {
                Logger.Warn($"Apt file '{MovieName}' imports '{import.Name}' from '{import.Movie}', which exports no such name; skipping the import.");
                continue;
            }

            //place the exported character inside our movie. Both indices are trusted by the
            //format but not by us: Age of the Ring ships apt files (reachable from Palantir.apt)
            //whose import slot is past the end of the importing movie's character table, and an
            //out-of-range write there used to take the whole process down mid-match rather than
            //costing one widget.
            if (import.Character >= Movie.Characters.Count ||
                export.Character >= importApt.Movie.Characters.Count)
            {
                Logger.Warn($"Apt file '{MovieName}' import '{import.Name}' from '{import.Movie}' is out of range " +
                            $"(slot {import.Character} of {Movie.Characters.Count}, source {export.Character} of {importApt.Movie.Characters.Count}); skipping the import.");
                continue;
            }

            Movie.Characters[(int)import.Character] = importApt.Movie.Characters[(int)export.Character];
        }
    }

    public static AptFile FromFileSystemEntry(FileSystemEntry entry)
    {
        using (var stream = entry.Open())
        using (var reader = new BinaryReader(stream, Encoding.ASCII, true))
        {
            //check if this is a valid apt file
            var magic = reader.ReadFixedLengthString(8);
            if (magic != "Apt Data")
            {
                throw new InvalidDataException();
            }

            //load the corresponding const entry
            var constPath = Path.ChangeExtension(entry.FilePath, ".const");
            var constEntry = entry.FileSystem.GetFile(constPath);
            if (constEntry == null)
            {
                throw new FileNotFoundException($"Cannot find constant data for apt file '{entry.FilePath}'", constPath);
            }
            var constFile = ConstantData.FromFileSystemEntry(constEntry);

            var aptName = Path.GetFileNameWithoutExtension(entry.FilePath);

            var apt = new AptFile(constFile, entry.FileSystem, aptName);
            apt.Parse(reader, Path.GetDirectoryName(entry.FilePath));

            return apt;
        }
    }

    public static AptFile CreateEmpty(string name, int width, int height, int millisecondsPerFrame)
    {
        var constData = new ConstantData();
        var apt = new AptFile(constData, null, name)
        {
            ImageMap = new ImageMap(),
            GeometryMap = new Dictionary<uint, Geometry>()
        };
        apt.Movie = Movie.CreateEmpty(apt, width, height, millisecondsPerFrame);
        return apt;
    }
}
