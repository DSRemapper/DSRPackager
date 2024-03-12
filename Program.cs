using System.CommandLine;
using System.IO.Compression;

namespace DSRPackager
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            Option<FileInfo> inputFile = new(
                name: "--plugin",
                description: "The folder with the plugin files")
            {IsRequired=true };
            inputFile.AddAlias("-p");
            Option<FileInfo> outputFile = new(
                name: "--output",
                description: "The output zip file",
                getDefaultValue: () => new("./plugin.zip"));
            outputFile.AddAlias("-o");
            Option<string[]> ignoreFiles = new(
                name: "--ignore",
                description: "The file to ignore on the packaging process",
                getDefaultValue: () => ["DSRemapper.Core.dll", "DSRemapper.Framework.dll", "FireLibs.Logging.dll"]);
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

            RootCommand rootCommand = new("DSRemapper utility for packaging the plugins")
            { TreatUnmatchedTokensAsErrors = true };
            rootCommand.AddOption(inputFile);
            rootCommand.AddOption(outputFile);
            rootCommand.AddOption(ignoreFiles);
            rootCommand.AddOption(fileExtensions);
            rootCommand.AddOption(writeOver);

            rootCommand.SetHandler(PluginPackager, inputFile, outputFile, ignoreFiles, fileExtensions, writeOver);

            return await rootCommand.InvokeAsync(args);
        }

        static void PluginPackager(FileInfo inputFile, FileInfo outputFile, string[] ignoredFiles, string[] fileExtensions, bool writeOver=false)
        {
            Console.WriteLine("Starting plugin packaging...");
            Console.WriteLine($"Plugin folder: {inputFile.FullName}");
            Console.WriteLine($"Plugin file: {outputFile.FullName} ({(writeOver?"override":"not override")})");

            if (!Directory.Exists(inputFile.FullName))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Packaging directory doesn't exists");
                Console.ForegroundColor = ConsoleColor.White;
                return;
            }
            if (!writeOver && File.Exists(outputFile.FullName))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Output file already exists");
                Console.ForegroundColor = ConsoleColor.White;
                return;
            }

            FileStream fileStream = new(outputFile.FullName,FileMode.Create);
            ZipArchive pluginFile = new(fileStream,ZipArchiveMode.Update);

            ignoredFiles = ignoredFiles.Select((f) => Path.Combine(inputFile.FullName, f)).ToArray();
            Console.WriteLine("\nIgnored files:");
            foreach (string file in ignoredFiles)
            {
                Console.WriteLine($"{file}");
            }

            Console.WriteLine("\nDetected files:");
            FileInfo[] files = Directory.GetFiles(inputFile.FullName,"*.*",SearchOption.AllDirectories)
                .Select((f)=> new FileInfo(f)).ToArray();
            foreach (FileInfo file in files)
            {
                bool forPackage = fileExtensions.Contains(file.Extension[1..]) && !ignoredFiles.Contains(file.FullName);
                string zipPath = Path.GetRelativePath(inputFile.FullName, file.FullName);
                Console.WriteLine($"{zipPath} ({(forPackage?"packaging...":"ignored")})");
                if (forPackage)
                {
                    ZipArchiveEntry entry = pluginFile.CreateEntry(zipPath);
                    Stream sEntry = entry.Open();
                    FileStream sFile = file.OpenRead();
                    sFile.CopyTo(sEntry);
                    sFile.Close();
                    sEntry.Close();
                }
            }
            pluginFile.Dispose();
            fileStream.Close();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nPlugin file successfully created");
            Console.ForegroundColor = ConsoleColor.White;
        }
    }
}