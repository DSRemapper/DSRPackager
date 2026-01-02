using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using DSRemapper.Core.CDN;
using AsmResolver.DotNet.Bundles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Bcpg;

namespace DSRPackager
{
    class Program
    {
        const string CoreDep = "DSRemapper.Core";
        const string FrameDep = "DSRemapper.Framework";
        const string ManifestFileName = "manifest.json";
        const string DSRKey = "DSR_GPG_KEY";
        const string DSRPass = "DSR_GPG_PASS";

        static readonly Option<string> pluginName = new("--name", "-n")
        {
            Description = "The name of the plugin",
            Required = true
        };
        static readonly Option<string> pluginDescription = new("--description", "-d")
        {
            Description = "The description of the plugin",
            DefaultValueFactory = (ar) => ""
        };
        static readonly Option<FileInfo> inputFile = new("--plugin", "-p")
        {
            Description = "The folder with the plugin files",
            Required = true
        };
        static readonly Option<FileInfo> outputFile = new("--output", "-o")
        {
            Description = "The output zip file",
            DefaultValueFactory = (ar) => new("./plugin.zip")
        };
        static readonly Option<string[]> ignoreFiles = new("--ignore", "-i")
        {
            Description = "The file to ignore on the packaging process",
            DefaultValueFactory = (ar) => ["DSRemapper.Core.dll", "DSRemapper.Framework.dll", "FireLibs.Logging.dll", ManifestFileName]
        };
        static readonly Option<string[]> fileExtensions = new("--extensions", "-e")
        {
            Description = "The extensions of files to package",
            DefaultValueFactory = (ar) => ["dll", "png", "ndll", "so", "dylib"]
        };
        static readonly Option<bool> writeOver = new(
            "--override", "-w", "-y")
        {
            Description = "If present overrides any file existing file in the output path",
            DefaultValueFactory = (ar) => false
        };
        static readonly Option<bool> throwError = new("--throw-error", "-t")
        {
            Description = "By default the program will handle the errors gracefully. If this flag is added the program will throw the error to the terminal.",
            DefaultValueFactory = (ar) => false,
        };
        static readonly Option<bool> appZip = new("--app-zip", "-a")
        {
            Description = "If this flag is set, DSRPackager will try to package the DSRemapper app instead of a plugin. This is intended ONLY for the official DSRemapper app CI workflow.",
            DefaultValueFactory = (ar) => false,
        };
        static readonly Option<string[]> downloadLinks = new("--links", "-l")
        {
            Description = "Use this tag to add download links for the diferent platforms in the format of \"OS:Link\" or \"OS=Link\". If your plugin is multi-platform use only one link with 'all' as OS. The supported platforms are: 'win', 'linux', 'osx', 'freebsd' and 'all'",
            Arity = ArgumentArity.ZeroOrMore,
            DefaultValueFactory = (ar) => [],
        };
        static readonly Option<bool> encryptManifest = new("--sign", "-s")
        {
            Description = $"If this tag is set, DSRPackager will check for the '{DSRKey}' (private key) and '{DSRPass}' enviroment keys and try to sign the manifest collection file, generating a '.asc' file.",
            DefaultValueFactory = (ar) => false,
        };
        static int Main(string[] args)
        {
            RootCommand rootCommand = new("DSRemapper utility for packaging the plugins")
            {
                TreatUnmatchedTokensAsErrors = true
            };
            rootCommand.Add(pluginName);
            rootCommand.Add(pluginDescription);
            rootCommand.Add(inputFile);
            rootCommand.Add(outputFile);
            rootCommand.Add(ignoreFiles);
            rootCommand.Add(fileExtensions);
            rootCommand.Add(writeOver);
            rootCommand.Add(throwError);
            rootCommand.Add(appZip);
            rootCommand.Add(downloadLinks);
            rootCommand.Add(encryptManifest);

            rootCommand.SetAction(CommandWrapper);

            ParseResult result = rootCommand.Parse(args);

            return result.Invoke();
        }

        static void CommandWrapper(ParseResult result)
        {
            Dictionary<OSPlatform, string> links = [];
            foreach (string entry in result.GetValue(downloadLinks) ?? [])
            {
                string[] parts = entry.Split(['=', ':'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                    links.Add(MapToOSPlatform(parts[0]), parts[1]);
                else
                    Console.Error.WriteLine($"Invalid format: '{entry}'. Use \"OS:Link\" or \"OS=Link\"");
            }

            if (result.GetValue(appZip))
            {
                AppPackager(result.GetValue(throwError), result.GetValue(pluginName)!,
                    result.GetValue(pluginDescription)!, result.GetValue(inputFile)!,
                    result.GetValue(outputFile)!, links,
                    result.GetValue(writeOver), result.GetValue(encryptManifest));
            }
            else
            {
                PluginPackager(result.GetValue(throwError), result.GetValue(pluginName)!,
                    result.GetValue(pluginDescription)!, result.GetValue(inputFile)!,
                    result.GetValue(outputFile)!, result.GetValue(ignoreFiles)!,
                    result.GetValue(fileExtensions)!, links,
                    result.GetValue(writeOver), result.GetValue(encryptManifest));
            }
        }

        static OSPlatform MapToOSPlatform(string input)
        {
            return input.ToLower().Trim() switch
            {
                "win" or "windows" or "win32" or "win64" => OSPlatform.Windows,
                "linux" or "ubuntu" or "debian" or "linux-x64" => OSPlatform.Linux,
                "osx" or "macos" or "apple" or "darwin" => OSPlatform.OSX,
                "freebsd" => OSPlatform.FreeBSD,
                "all" => Manifest.AllPlatforms,
                _ => throw new ArgumentException($"Unsupported platform identifier: {input}")
            };
        }

        static void PluginPackager(bool throwError, string pluginName, string pluginDescription, FileInfo inputFile, FileInfo outputFile, string[] ignoredFiles, string[] fileExtensions, Dictionary<OSPlatform, string> links, bool writeOver = false, bool encrypt = false)
        {
            ConsoleColor originalColor = Console.ForegroundColor;
            try
            {
                if (!ValidateInputs(inputFile, outputFile, writeOver))
                    return;

                DirectoryInfo pluginDir = inputFile.Directory!;
                DirectoryInfo outDir = outputFile.Directory!;

                Console.WriteLine("Starting plugin packaging...");
                Console.WriteLine($"Plugin folder: {pluginDir.FullName}");
                Console.WriteLine($"Plugin file: {outputFile.FullName} ({(writeOver ? "override" : "not override")})");


                var (pluginVer, initialCoreVer, initialFrameVer) = GetPluginInfo(inputFile);
                List<FileInfo> filesToPackage = GetFilesToPackage(pluginDir, fileExtensions, ignoredFiles);
                var (coreVer, frameVer) = GetDependencyVersions(filesToPackage, initialCoreVer, initialFrameVer);

                Manifest manifest = new(pluginName, pluginVer, coreVer, frameVer, pluginDescription);

                CreateZipArchive(outputFile, pluginDir, filesToPackage, manifest);
                string hash = GetFileHash(outputFile);
                Dictionary<OSPlatform, DownloadInfo> infos = new(links.Select(kv => new KeyValuePair<OSPlatform, DownloadInfo>(kv.Key, new DownloadInfo(kv.Value, hash))));
                manifest.SetDownloadLinks(infos);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nPlugin file successfully created");
                Console.ForegroundColor = originalColor;
                UpdateManifestCollection(outDir, manifest, throwError, encrypt);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nAn error occurred: {ex.Message}");
                Console.ForegroundColor = originalColor;
                if (throwError)
                    throw;
            }
        }

        static void AppPackager(bool throwError, string appName, string appDescription, FileInfo inputFile, FileInfo outputFile, Dictionary<OSPlatform, string> links, bool writeOver = false, bool encrypt = false)
        {

            ConsoleColor originalColor = Console.ForegroundColor;
            try
            {
                if (!ValidateInputs(inputFile, outputFile, writeOver))
                    return;

                DirectoryInfo appDir = inputFile.Directory!;
                DirectoryInfo outDir = outputFile.Directory!;

                Console.WriteLine("Starting plugin packaging...");
                Console.WriteLine($"Plugin folder: {appDir.FullName}");
                Console.WriteLine($"Plugin file: {outputFile.FullName} ({(writeOver ? "override" : "not override")})");


                var (pluginVer, coreVer, frameVer) = GetAppInfo(inputFile);
                FileInfo[] filesToPackage = appDir.GetFiles("*.*", SearchOption.AllDirectories);

                Manifest manifest = new(appName, pluginVer, coreVer, frameVer, appDescription);

                CreateZipArchive(outputFile, appDir, filesToPackage, manifest);
                string hash = GetFileHash(outputFile);
                Dictionary<OSPlatform, DownloadInfo> infos = new(links.Select(kv => new KeyValuePair<OSPlatform, DownloadInfo>(kv.Key, new DownloadInfo(kv.Value, hash))));
                manifest.SetDownloadLinks(infos);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\nApp zip successfully created");
                Console.ForegroundColor = originalColor;
                UpdateManifestCollection(outDir, manifest, throwError, encrypt);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nAn error occurred: {ex.Message}");
                Console.ForegroundColor = originalColor;
                if (throwError)
                    throw;
            }
        }
        private static string GetFileHash(FileInfo file)
        {
            FileStream sf = file.OpenRead();
            byte[] hash = SHA256.HashData(sf);
            sf.Close();
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static bool ValidateInputs(FileInfo inputFile, FileInfo outputFile, bool writeOver)
        {
            ConsoleColor originalColor = Console.ForegroundColor;
            DirectoryInfo? pluginDir = inputFile.Directory;

            if (pluginDir == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("The input file's directory could not be determined.");
                Console.ForegroundColor = originalColor;
                return false;
            }

            if (!pluginDir.Exists)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Packaging directory doesn't exists");
                Console.ForegroundColor = originalColor;
                return false;
            }

            if (!writeOver && File.Exists(outputFile.FullName))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Output file already exists");
                Console.ForegroundColor = originalColor;
                return false;
            }

            return true;
        }

        private static (Version pluginVer, Version? coreVer, Version? frameVer) GetPluginInfo(FileInfo inputFile)
        {
            Assembly pluginAssembly = Assembly.LoadFrom(inputFile.FullName);
            Version pluginVer = pluginAssembly.GetName().Version ?? new Version(0, 0, 1);
            Version? coreVer = pluginAssembly.GetReferencedAssemblies().FirstOrDefault((an) => an.Name?.Equals(CoreDep) ?? false)?.Version;
            Version? frameVer = pluginAssembly.GetReferencedAssemblies().FirstOrDefault((an) => an.Name?.Equals(FrameDep) ?? false)?.Version;
            return (pluginVer, coreVer, frameVer);
        }
        private static (Version appVer, Version? coreVer, Version? frameVer) GetAppInfo(FileInfo inputFile)
        {
            Version[] versions = [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)];
            List<string> assemblies = ["DSRemapper.ServerApp.dll", $"{CoreDep}.dll", $"{FrameDep}.dll"];
            BundleManifest manifest = BundleManifest.FromFile(inputFile.FullName);
            foreach (var file in manifest.Files)
            {
                int index = assemblies.IndexOf(file.RelativePath);
                if (assemblies.Contains(file.RelativePath))
                {
                    Assembly assembly = Assembly.Load(file.GetData());
                    versions[index] = assembly.GetName().Version ?? new(0, 0, 1);
                }
            }
            return (versions[0], versions[1], versions[2]);
        }

        private static List<FileInfo> GetFilesToPackage(DirectoryInfo pluginDir, string[] fileExtensions, string[] ignoredFiles)
        {
            List<FileInfo> filesToPackage = [];
            List<string> fullIgnoredPaths = [.. ignoredFiles.Select(f => Path.Combine(pluginDir.FullName, f))];

            Console.WriteLine("\nIgnored files:");
            foreach (string file in ignoredFiles)
                Console.WriteLine($"{file}");

            Console.WriteLine("\nDetected files:");
            FileInfo[] allFiles = pluginDir.GetFiles("*.*", SearchOption.AllDirectories);
            foreach (FileInfo file in allFiles)
            {
                string ext = file.Extension.Length > 1 ? file.Extension[1..] : "";
                bool forPackage = fileExtensions.Contains(ext) && !fullIgnoredPaths.Contains(file.FullName);
                string zipPath = Path.GetRelativePath(pluginDir.FullName, file.FullName);
                Console.WriteLine($"{zipPath} ({(forPackage ? "packaging..." : "ignored")})");
                if (forPackage)
                    filesToPackage.Add(file);
            }
            return filesToPackage;
        }

        private static (Version? coreVer, Version? frameVer) GetDependencyVersions(IEnumerable<FileInfo> files, Version? initialCoreVer, Version? initialFrameVer)
        {
            var coreVer = initialCoreVer;
            var frameVer = initialFrameVer;

            foreach (var file in files.Where(f => f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                Assembly assembly = Assembly.LoadFrom(file.FullName);
                AssemblyName[] references = assembly.GetReferencedAssemblies();
                Version? coreDepVer = references.FirstOrDefault((an) => an.Name?.Equals(CoreDep) ?? false)?.Version;
                Version? frameDepVer = references.FirstOrDefault((an) => an.Name?.Equals(FrameDep) ?? false)?.Version;
                if (coreDepVer != null && (coreVer == null || coreDepVer > coreVer))
                    coreVer = coreDepVer;
                if (frameDepVer != null && (frameVer == null || frameDepVer > frameVer))
                    frameVer = frameDepVer;
            }
            return (coreVer, frameVer);
        }

        private static void CreateZipArchive(FileInfo outputFile, DirectoryInfo pluginDir, IEnumerable<FileInfo> filesToPackage, Manifest manifest)
        {
            using FileStream fileStream = new(outputFile.FullName, FileMode.Create);
            using ZipArchive pluginFile = new(fileStream, ZipArchiveMode.Create);

            foreach (var file in filesToPackage)
            {
                string zipPath = Path.GetRelativePath(pluginDir.FullName, file.FullName);
                pluginFile.CreateEntryFromFile(file.FullName, zipPath);
            }

            var manifestEntry = pluginFile.CreateEntry(ManifestFileName);
            using var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8);
            writer.Write(manifest.SerializeToJson(true));
        }

        private static void UpdateManifestCollection(DirectoryInfo outDir, Manifest manifest, bool throwError, bool encrypt = false)
        {
            ConsoleColor originalColor = Console.ForegroundColor;
            try
            {
                FileInfo manifestFile = new(Path.Combine(outDir.FullName, ManifestFileName));

                ManifestCollection collection = [];
                if (manifestFile.Exists)
                {
                    Console.WriteLine("\nAdding manifest to the manifest collection file.");
                    collection = ManifestCollection.FromJson(File.ReadAllText(manifestFile.FullName));
                }
                Console.WriteLine("Writing manifest collection file.");
                if (collection.TryGetValue(manifest, out Manifest curManifest))
                {
                    var newPlatforms = manifest.DownloadLinks.Where(kv => !curManifest.DownloadLinks.ContainsKey(kv.Key));
                    foreach (var platform in newPlatforms)
                    {
                        curManifest.SetOSDownloadLink(platform.Key, platform.Value);
                    }
                    collection.Remove(curManifest);
                    collection.Add(curManifest);
                }
                else
                    collection.Add(manifest);
                File.WriteAllText(manifestFile.FullName, collection.SerializeToJson());
                if (encrypt)
                    EncryptManifestCollection(manifestFile);
            }
            catch
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nError adding the manifest to the collection");
                Console.ForegroundColor = originalColor;
                if (throwError)
                    throw;
            }
        }

        static bool EncryptManifestCollection(FileInfo manifestFile)
        {
            string rawKey = Environment.GetEnvironmentVariable(DSRKey) ?? "";
            byte[] key = Encoding.UTF8.GetBytes(rawKey);
            rawKey = null!;
            string rawPass = Environment.GetEnvironmentVariable(DSRPass) ?? "";
            char[] pass = rawPass.ToCharArray();
            rawPass = null!;

            try
            {
                if (key.Length > 0 && pass.Length > 0)
                {
                    PgpPrivateKey privateKey;
                    using (MemoryStream keyStream = new(key))
                    {
                        PgpSecretKeyRingBundle secretKeyBundle = new(PgpUtilities.GetDecoderStream(keyStream));
                        PgpSecretKey secretKey = secretKeyBundle.GetKeyRings()
                            .Cast<PgpSecretKeyRing>()
                            .SelectMany(kr => kr.GetSecretKeys().Cast<PgpSecretKey>())
                            .FirstOrDefault(k => k.IsSigningKey)
                            ?? throw new PgpException("No signing key found in the provided stream.");

                        privateKey = secretKey.ExtractPrivateKey(pass);
                        keyStream.Close();
                    }

                    PgpSignatureGenerator sGen = new(privateKey.PublicKeyPacket.Algorithm, HashAlgorithmTag.Sha256);
                    sGen.InitSign(PgpSignature.BinaryDocument, privateKey);

                    using (FileStream manifestStream = manifestFile.OpenRead())
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead;
                        while ((bytesRead = manifestStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sGen.Update(buffer, 0, bytesRead);
                        }
                        manifestStream.Close();
                    }

                    FileInfo armoredFile = new($"{manifestFile.FullName}.asc");
                    using (FileStream armoredStream = armoredFile.Open(FileMode.Create))
                    using (ArmoredOutputStream armoredOut = new(armoredStream))
                    using (BcpgOutputStream bcpgOut = new(armoredOut))
                    {
                        sGen.Generate().Encode(bcpgOut);
                    }

                    privateKey = null!;
                    return true;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                Array.Clear(pass);
            }
            return false;
        }
    }
}