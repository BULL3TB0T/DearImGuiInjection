using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DearImGuiInjection.Tools;

internal static class Program
{
    private static int Main()
    {
        string rootDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string dataDirectory = Directory.EnumerateDirectories(rootDirectory, "*_Data", SearchOption.TopDirectoryOnly).FirstOrDefault();
        bool hasGameAssembly = false;
        bool hasGlobalMetadata = false;
        string managedDirectory = null;
        string mscorlibPath = null;
        string netstandardPath = null;
        if (dataDirectory != null)
        {
            hasGameAssembly = File.Exists(Path.Combine(rootDirectory, "GameAssembly.dll"));
            hasGlobalMetadata = File.Exists(Path.Combine(dataDirectory, "il2cpp_data", "Metadata", "global-metadata.dat"));
            managedDirectory = Path.Combine(dataDirectory, "Managed");
            if (Directory.Exists(managedDirectory))
            {
                string mscorlibCandidate = Path.Combine(managedDirectory, "mscorlib.dll");
                if (File.Exists(mscorlibCandidate))
                    mscorlibPath = mscorlibCandidate;
                string netstandardCandidate = Path.Combine(managedDirectory, "netstandard.dll");
                if (File.Exists(netstandardCandidate))
                    netstandardPath = netstandardCandidate;
            }
        }
        string targetFramework = null;
        if (netstandardPath != null)
        {
            AssemblyName netstandardAssemblyName = AssemblyName.GetAssemblyName(netstandardPath);
            Version netstandardVersion = netstandardAssemblyName.Version;
            if (netstandardVersion != null)
            {
                Console.WriteLine($"Netstandard {netstandardVersion} has been found.");
                targetFramework = $"netstandard{netstandardVersion.Major}.{netstandardVersion.Minor}";
            }
            else
            {
                Console.WriteLine("Netstandard has been found but version could not be read.");
            }
        }
        if (mscorlibPath != null)
        {
            AssemblyName mscorlibAssemblyName = AssemblyName.GetAssemblyName(mscorlibPath);
            Version mscorlibVersion = mscorlibAssemblyName.Version;
            if (mscorlibVersion != null)
            {
                Console.WriteLine($"Mscorlib {mscorlibVersion} has been found.");
                targetFramework = mscorlibVersion.Major >= 4 ? "net462" : "net35";
            }
            else
                Console.WriteLine("Mscorlib has been found but version could not be read.");
        }
        if (hasGameAssembly || hasGlobalMetadata)
        {
            if (hasGameAssembly)
                Console.WriteLine($"Game Assembly has been found.");
            if (hasGlobalMetadata)
                Console.WriteLine($"Global Metadata has been found.");
            targetFramework = "net6.0";
        }
        if (targetFramework != null)
        {
            Console.WriteLine($"Unity {(targetFramework == "net6.0" ? "IL2CPP" : "Mono")} has been detected.");
            Console.WriteLine($"Expected Target Framework: {targetFramework}");
            if (targetFramework == "net35")
                Console.WriteLine("Warning: net35 is not supported by DearImGuiInjection.");
        }
        else
        {
            Console.WriteLine("Unable to determine Unity backend or Target Framework.");
            Console.WriteLine("Note: No *_Data folder was found in the current directory.");
        }
        Console.WriteLine("Press any key to continue...");
        Console.ReadKey();
        return 0;
    }
}
