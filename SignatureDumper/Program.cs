using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SignatureDumper
{
    internal class Program
    {
        // In Falsus는 게임 코드가 Assembly-CSharp가 아니라 Game.* 어셈블리들로 쪼개져 있다.
        // (Assembly-CSharp.dll은 8KB짜리 껍데기) 그래서 Il2CppGame*.dll 전부 + 아래 목록을 같이 덤프한다.
        static readonly string[] ExtraAssemblies =
        {
            "Il2Cpp__Generated.dll",
            "Il2CppStr.dll",
            "Il2CppGramma.dll",
        };

        static void Main(string[] args)
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("  In Falsus Assembly-CSharp Signature Dumper");
            Console.WriteLine("==========================================================");

            // --all : Il2CppAssemblies의 모든 어셈블리(Unity/BCL/서드파티 포함)를 Decompiled_Full 로 덤프
            bool dumpAll = args.Any(a => a.Equals("--all", StringComparison.OrdinalIgnoreCase));
            args = args.Where(a => !a.StartsWith("--")).ToArray();

            string gameDir = @"H:\steam\steamapps\common\In Falsus";

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));

            if (!File.Exists(Path.Combine(solutionRoot, "In Falsus mods.slnx")))
            {
                solutionRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", ".."));
            }

            string outputDir = solutionRoot;

            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])) gameDir = args[0];
            if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])) outputDir = args[1];

            Console.WriteLine($"[Config] Game Directory:   {gameDir}");
            Console.WriteLine($"[Config] Output Directory: {outputDir}");
            Console.WriteLine("----------------------------------------------------------");

            try
            {
                string decompileFolder = dumpAll ? "Decompiled_Full" : "Decompiled";
                var targets = dumpAll ? CollectAllTargets(gameDir) : CollectTargets(gameDir);

                if (targets.Count == 0)
                {
                    Console.WriteLine("[Error] No target assemblies found. MelonLoader를 한 번 실행해 Il2CppAssemblies를 생성했는지 확인하세요.");
                    return;
                }

                Console.WriteLine($"[Config] Target Assemblies: {targets.Count}");
                foreach (var t in targets) Console.WriteLine($"           - {Path.GetFileName(t)}");
                Console.WriteLine("----------------------------------------------------------");

                bool first = true;
                foreach (var target in targets)
                {
                    Console.WriteLine();
                    var options = new SignatureDumperOptions
                    {
                        GameDirectory = gameDir,
                        TargetAssemblyPath = target,
                        OutputDirectory = outputDir,
                        DecompileFolderName = decompileFolder,
                        CleanDecompileFolder = first,
                        DumpCSharpSignatures = false,
                        DumpSummaryText = false,
                        DumpJsonMetadata = false,
                        DumpIndividualFiles = true
                    };

                    try
                    {
                        new AssemblySignatureDumper(options).Dump();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Warn] Skipped '{Path.GetFileName(target)}': {ex.Message}");
                    }

                    first = false;
                }

                PrintSummary(Path.Combine(outputDir, decompileFolder));
                Console.WriteLine("\n[Status] Signature Dumping Completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Error] Failed: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        static List<string> CollectTargets(string gameDir)
        {
            var targets = new List<string>();
            string il2cppDir = Path.Combine(gameDir, "MelonLoader", "Il2CppAssemblies");

            if (!Directory.Exists(il2cppDir))
            {
                // Mono 빌드 대비 폴백 (In Falsus는 Il2Cpp라 보통 여기까지 오지 않는다)
                foreach (var dataDir in Directory.GetDirectories(gameDir, "*_Data"))
                {
                    string managed = Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll");
                    if (File.Exists(managed)) targets.Add(managed);
                }
                return targets;
            }

            string mainAsm = Path.Combine(il2cppDir, "Assembly-CSharp.dll");
            if (File.Exists(mainAsm)) targets.Add(mainAsm);

            targets.AddRange(Directory.GetFiles(il2cppDir, "Il2CppGame*.dll").OrderBy(f => f));

            foreach (var extra in ExtraAssemblies)
            {
                string path = Path.Combine(il2cppDir, extra);
                if (File.Exists(path)) targets.Add(path);
            }

            return targets;
        }

        // --all 모드: Il2CppAssemblies 전체. 게임 코드를 마지막에 둬서, 전역 네임스페이스(Il2Cpp)에서
        // 파일 이름이 겹치면 게임 쪽 덤프가 Unity/BCL 쪽을 덮어쓰고 남도록 한다.
        static List<string> CollectAllTargets(string gameDir)
        {
            string il2cppDir = Path.Combine(gameDir, "MelonLoader", "Il2CppAssemblies");
            if (!Directory.Exists(il2cppDir)) return CollectTargets(gameDir);

            var gameTargets = CollectTargets(gameDir);
            var rest = Directory.GetFiles(il2cppDir, "*.dll")
                                .Where(f => !gameTargets.Contains(f, StringComparer.OrdinalIgnoreCase))
                                .OrderBy(f => f);

            return rest.Concat(gameTargets).ToList();
        }

        static void PrintSummary(string decompiledDir)
        {
            Console.WriteLine("\n----------------------------------------------------------");
            if (!Directory.Exists(decompiledDir))
            {
                Console.WriteLine("[Summary] Decompiled directory not found.");
                return;
            }

            var files = Directory.GetFiles(decompiledDir, "*.cs", SearchOption.AllDirectories);
            var namespaces = files
                .Select(f => Path.GetDirectoryName(f).Substring(decompiledDir.Length).TrimStart(Path.DirectorySeparatorChar))
                .GroupBy(ns => ns)
                .OrderByDescending(g => g.Count())
                .ToList();

            Console.WriteLine($"[Summary] Total type files : {files.Length}");
            Console.WriteLine($"[Summary] Total namespaces : {namespaces.Count}");
            Console.WriteLine("[Summary] Top namespaces:");
            foreach (var ns in namespaces.Take(20))
            {
                Console.WriteLine($"  - {(string.IsNullOrEmpty(ns.Key) ? "<root>" : ns.Key.Replace(Path.DirectorySeparatorChar, '.')),-50} : {ns.Count()} types");
            }
            Console.WriteLine("----------------------------------------------------------");
        }
    }
}
