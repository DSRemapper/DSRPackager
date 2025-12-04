using System.CommandLine;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using DSRemapper.Core;

namespace DSRPackager
{
    class Program
    {
        const string CoreDep = "DSRemapper.Core";
        const string FrameDep = "DSRemapper.Framework";
        const string manifestFileName = "manifest.json";
        static async Task<int> Main(string[] args)
        {
            Option<string> pluginName = new(
                name: "--name",
                description: "The name of the plugin")
            { IsRequired=true };
            pluginName.AddAlias("-n");
            Option<FileInfo> inputFile = new(
                name: "--plugin",
                description: "The folder with the plugin files")
            { IsRequired=true };
            inputFile.AddAlias("-p");
            Option<FileInfo> outputFile = new(
                name: "--output",
                description: "The output zip file",
                getDefaultValue: () => new("./plugin.zip"));
            outputFile.AddAlias("-o");
            Option<string[]> ignoreFiles = new(
                name: "--ignore",
                description: "The file to ignore on the packaging process",
                getDefaultValue: () => ["DSRemapper.Core.dll", "DSRemapper.Framework.dll", "FireLibs.Logging.dll", manifestFileName]);
            ignoreFiles.AddAlias("-i");
            Option<string[]> fileExtensions = new(
                name: "--extensions",
                description: "The extensions of files to package",
                getDefaultValue: () => ["dll", "png", "ndll","so","dylib"]);
            fileExtensions.AddAlias("-e");
            Option<bool> writeOver = new(
                name: "--override",
                description: "If present overrides any file existing file in the output path",
                getDefaultValue: () => false);
            writeOver.AddAlias("-w");
            writeOver.AddAlias("-y");

            RootCommand rootCommand = new("DSRemapper utility for packaging the plugins")
            { TreatUnmatchedTokensAsErrors = true };
            rootCommand.AddOption(pluginName);
            rootCommand.AddOption(inputFile);
            rootCommand.AddOption(outputFile);
            rootCommand.AddOption(ignoreFiles);
            rootCommand.AddOption(fileExtensions);
            rootCommand.AddOption(writeOver);

            rootCommand.SetHandler(PluginPackager, pluginName, inputFile, outputFile, ignoreFiles, fileExtensions, writeOver);

            return await rootCommand.InvokeAsync(args);
        }

        static void PluginPackager(string pluginName, FileInfo inputFile, FileInfo outputFile, string[] ignoredFiles, string[] fileExtensions, bool writeOver=false)
        {
            ConsoleColor originalColor = Console.ForegroundColor;
            DirectoryInfo pluginDir = inputFile.Directory ?? throw new ArgumentNullException(nameof(inputFile), "The input file's directory could not be determined.");
            DirectoryInfo outDir = outputFile.Directory ?? throw new ArgumentNullException(nameof(outputFile), "The input file's directory could not be determined.");
            
            Assembly pluginAssembly = Assembly.LoadFrom(inputFile.FullName);
            Version pluginVer = pluginAssembly.GetName().Version ?? new Version(1,0,0);
            Version? coreVer = pluginAssembly.GetReferencedAssemblies().FirstOrDefault((an) => an.Name?.Equals(CoreDep) ?? false)?.Version;
            Version? frameVer = pluginAssembly.GetReferencedAssemblies().FirstOrDefault((an) => an.Name?.Equals(FrameDep) ?? false)?.Version;;

            Console.WriteLine("Starting plugin packaging...");
            Console.WriteLine($"Plugin folder: {pluginDir.FullName}");
            Console.WriteLine($"Plugin file: {outputFile.FullName} ({(writeOver?"override":"not override")})");

            if (!pluginDir.Exists)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Packaging directory doesn't exists");
                Console.ForegroundColor = originalColor;
                return;
            }
            if (!writeOver && File.Exists(outputFile.FullName))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Output file already exists");
                Console.ForegroundColor = originalColor;
                return;
            }

            FileStream fileStream = new(outputFile.FullName,FileMode.Create);
            ZipArchive pluginFile = new(fileStream,ZipArchiveMode.Update);

            ignoredFiles = ignoredFiles.Select((f) => Path.Combine(pluginDir.FullName, f)).ToArray();
            Console.WriteLine("\nIgnored files:");
            foreach (string file in ignoredFiles)
                Console.WriteLine($"{file}");

            Console.WriteLine("\nDetected files:");
            FileInfo[] files = pluginDir.GetFiles("*.*", SearchOption.AllDirectories);
            foreach (FileInfo file in files)
            {
                string ext = file.Extension[1..];
                bool forPackage = fileExtensions.Contains(ext) && !ignoredFiles.Contains(file.FullName);
                string zipPath = Path.GetRelativePath(pluginDir.FullName, file.FullName);
                Console.WriteLine($"{zipPath} ({(forPackage?"packaging...":"ignored")})");
                if (forPackage)
                {
                    if (ext == "dll"){
                        Assembly assembly = Assembly.LoadFrom(file.FullName);
                        AssemblyName[] references = assembly.GetReferencedAssemblies();
                        Version? coreDepVer = references.FirstOrDefault((an) => an.Name?.Equals(CoreDep) ?? false)?.Version;
                        Version? frameDepVer = references.FirstOrDefault((an) => an.Name?.Equals(FrameDep) ?? false)?.Version;
                        if (coreDepVer != null && (coreVer == null || coreDepVer > coreVer))
                            coreVer = coreDepVer;
                        if (frameDepVer != null && (frameVer == null || frameDepVer > frameVer))
                            frameVer = frameDepVer;
                    }

                    ZipArchiveEntry entry = pluginFile.CreateEntry(zipPath);
                    Stream sEntry = entry.Open();
                    FileStream sFile = file.OpenRead();
                    sFile.CopyTo(sEntry);
                    sFile.Close();
                    sEntry.Close();
                }
            }

            string manifest = new PluginManifest(pluginName, pluginVer, coreVer, frameVer).SerializeToJson();
            ZipArchiveEntry manifestEntry = pluginFile.CreateEntry(manifestFileName);
            Stream sManifest = manifestEntry.Open();
            sManifest.Write(Encoding.ASCII.GetBytes(manifest));
            sManifest.Close();
            
            pluginFile.Dispose();
            fileStream.Close();

            FileInfo manifestFile = new FileInfo(Path.Combine(outDir.FullName,manifestFileName));
            if (!manifestFile.Exists)
                File.WriteAllText(manifestFile.FullName, manifest);
            else{
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("The external 'manifest.json' file could not be created. You can extract it from the plugin file.");
                Console.ForegroundColor = originalColor;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nPlugin file successfully created");
            Console.ForegroundColor = originalColor;
        }
    }
}