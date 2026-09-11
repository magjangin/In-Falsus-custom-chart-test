using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SignatureDumper
{
    public class TypeSignatureInfo
    {
        public string Namespace { get; set; }
        public string Name { get; set; }
        public string RawTypeName { get; set; }
        public string FullName { get; set; }
        public string Kind { get; set; } // class, struct, interface, enum, delegate
        public string AccessModifiers { get; set; }
        public string BaseType { get; set; }
        public List<string> Interfaces { get; set; } = new List<string>();
        public List<string> EnumMembers { get; set; } = new List<string>();
        public List<string> Fields { get; set; } = new List<string>();
        public List<string> Properties { get; set; } = new List<string>();
        public List<string> Events { get; set; } = new List<string>();
        public List<string> Constructors { get; set; } = new List<string>();
        public List<string> Methods { get; set; } = new List<string>();
    }

    public class SignatureDumperOptions
    {
        public string GameDirectory { get; set; } = @"H:\steam\steamapps\common\In Falsus";
        public string TargetAssemblyPath { get; set; }
        public string OutputDirectory { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
        public bool DumpCSharpSignatures { get; set; } = false;
        public bool DumpJsonMetadata { get; set; } = false;
        public bool DumpSummaryText { get; set; } = false;
        public bool DumpIndividualFiles { get; set; } = true;
        public string DecompileFolderName { get; set; } = "Decompiled";
        // false로 두면 이전 어셈블리의 덤프 결과를 지우지 않고 같은 Decompiled 트리에 이어서 쓴다.
        public bool CleanDecompileFolder { get; set; } = true;
    }

    public class AssemblySignatureDumper
    {
        private readonly SignatureDumperOptions _options;

        public AssemblySignatureDumper(SignatureDumperOptions options)
        {
            _options = options ?? new SignatureDumperOptions();
        }

        public void Dump()
        {
            string assemblyPath = _options.TargetAssemblyPath;

            if (string.IsNullOrEmpty(assemblyPath))
            {
                // Il2Cpp: MelonLoader\Il2CppAssemblies, Mono: <Game>_Data\Managed
                var defaultPaths = new List<string>
                {
                    Path.Combine(_options.GameDirectory, "MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll")
                };

                if (Directory.Exists(_options.GameDirectory))
                {
                    foreach (var dataDir in Directory.GetDirectories(_options.GameDirectory, "*_Data"))
                    {
                        defaultPaths.Add(Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll"));
                    }
                }

                assemblyPath = defaultPaths.FirstOrDefault(File.Exists);

                if (assemblyPath == null)
                {
                    throw new FileNotFoundException(
                        "Assembly-CSharp.dll not found in default paths: " + string.Join(" | ", defaultPaths));
                }
            }

            Console.WriteLine($"[SignatureDumper] Target Assembly: {assemblyPath}");
            string searchDir = Path.GetDirectoryName(assemblyPath);

            var assemblyCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var reqName = new AssemblyName(args.Name).Name;
                    var asmName = reqName + ".dll";

                    if (assemblyCache.TryGetValue(reqName, out var cached)) return cached;

                    var candidatePaths = new List<string>
                    {
                        Path.Combine(searchDir, asmName),
                        Path.Combine(_options.GameDirectory, "MelonLoader", "Il2CppAssemblies", asmName),
                        Path.Combine(_options.GameDirectory, "MelonLoader", "net6", asmName),
                        Path.Combine(_options.GameDirectory, "MelonLoader", "net472", asmName),
                        Path.Combine(_options.GameDirectory, "MelonLoader", "net35", asmName),
                        Path.Combine(_options.GameDirectory, "MelonLoader", "Dependencies", asmName),
                        Path.Combine(_options.GameDirectory, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator", "Cpp2IL", "cpp2il_out", asmName),
                        Path.Combine(_options.GameDirectory, "MelonLoader", "Dependencies", "Il2CppAssemblyGenerator", asmName),
                        Path.Combine(_options.GameDirectory, "MelonLoader", "Managed", asmName)
                    };

                    if (Directory.Exists(_options.GameDirectory))
                    {
                        foreach (var dataDir in Directory.GetDirectories(_options.GameDirectory, "*_Data"))
                        {
                            candidatePaths.Add(Path.Combine(dataDir, "Managed", asmName));
                        }
                    }

                    foreach (var path in candidatePaths)
                    {
                        if (File.Exists(path))
                        {
                            var loaded = Assembly.LoadFrom(path);
                            assemblyCache[reqName] = loaded;
                            return loaded;
                        }
                    }
                }
                catch { }
                return null;
            };

            Assembly asm = Assembly.LoadFrom(assemblyPath);
            Type[] types;

            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
                Console.WriteLine($"[SignatureDumper] Warning: Loaded {types.Length} types with some unresolved dependencies.");
            }

            Console.WriteLine($"[SignatureDumper] Analyzing {types.Length} types...");

            List<TypeSignatureInfo> infos = new List<TypeSignatureInfo>();

            var orderedTypes = types.OrderBy(t => {
                try { return t.Namespace ?? ""; } catch { return ""; }
            }).ThenBy(t => {
                try { return t.Name ?? ""; } catch { return ""; }
            });

            foreach (var type in orderedTypes)
            {
                try
                {
                    infos.Add(AnalyzeType(type));
                }
                catch (Exception ex)
                {
                    string name = "<unknown>";
                    try { name = type.FullName ?? type.Name; } catch { }
                    Console.WriteLine($"[SignatureDumper] Error analyzing type '{name}': {ex.Message}");
                }
            }

            Directory.CreateDirectory(_options.OutputDirectory);
            string asmBaseName = Path.GetFileNameWithoutExtension(assemblyPath);

            if (_options.DumpCSharpSignatures)
            {
                string csPath = Path.Combine(_options.OutputDirectory, $"{asmBaseName}_Signatures.cs");
                WriteCSharpSignatures(csPath, asmBaseName, infos);
                Console.WriteLine($"[SignatureDumper] Dumped single C# signature file to: {csPath}");
            }

            if (_options.DumpIndividualFiles)
            {
                string decompileDir = Path.Combine(_options.OutputDirectory, _options.DecompileFolderName);
                WriteIndividualFiles(decompileDir, infos);
                Console.WriteLine($"[SignatureDumper] Generated {infos.Count} individual C# files in folder: {decompileDir}");
            }

            if (_options.DumpSummaryText)
            {
                string summaryPath = Path.Combine(_options.OutputDirectory, $"{asmBaseName}_Summary.txt");
                WriteSummaryText(summaryPath, asmBaseName, infos);
                Console.WriteLine($"[SignatureDumper] Dumped Summary to: {summaryPath}");
            }

            if (_options.DumpJsonMetadata)
            {
                string jsonPath = Path.Combine(_options.OutputDirectory, $"{asmBaseName}_Signatures.json");
                WriteJsonMetadata(jsonPath, infos);
                Console.WriteLine($"[SignatureDumper] Dumped JSON metadata to: {jsonPath}");
            }
        }

        private TypeSignatureInfo AnalyzeType(Type type)
        {
            var info = new TypeSignatureInfo
            {
                Namespace = type.Namespace ?? "<global>",
                Name = GetTypeName(type),
                RawTypeName = type.Name,
                FullName = type.FullName ?? type.Name
            };

            try
            {
                if (type.IsPublic || type.IsNestedPublic) info.AccessModifiers = "public";
                else if (type.IsNestedPrivate) info.AccessModifiers = "private";
                else if (type.IsNestedFamily) info.AccessModifiers = "protected";
                else if (type.IsNestedAssembly || type.IsNotPublic) info.AccessModifiers = "internal";
                else if (type.IsNestedFamORAssem) info.AccessModifiers = "protected internal";
                else if (type.IsNestedFamANDAssem) info.AccessModifiers = "private protected";
                else info.AccessModifiers = "internal";
            }
            catch { info.AccessModifiers = "public"; }

            try
            {
                if (type.IsEnum)
                {
                    info.Kind = "enum";
                    foreach (var name in Enum.GetNames(type))
                    {
                        try
                        {
                            var val = Convert.ChangeType(Enum.Parse(type, name), Enum.GetUnderlyingType(type));
                            info.EnumMembers.Add($"{name} = {val}");
                        }
                        catch
                        {
                            info.EnumMembers.Add(name);
                        }
                    }
                    return info;
                }
            }
            catch { }

            try
            {
                if (type.IsInterface) info.Kind = "interface";
                else if (typeof(Delegate).IsAssignableFrom(type)) info.Kind = "delegate";
                else if (type.IsValueType) info.Kind = "struct";
                else info.Kind = "class";
            }
            catch { info.Kind = "class"; }

            try
            {
                if (type.IsAbstract && type.IsSealed) info.AccessModifiers += " static";
                else if (type.IsAbstract && !type.IsInterface) info.AccessModifiers += " abstract";
                else if (type.IsSealed && !type.IsValueType) info.AccessModifiers += " sealed";
            }
            catch { }

            try
            {
                if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
                {
                    info.BaseType = GetTypeName(type.BaseType);
                }
            }
            catch { }

            try
            {
                foreach (var iface in type.GetInterfaces())
                {
                    try { info.Interfaces.Add(GetTypeName(iface)); } catch { }
                }
            }
            catch { }

            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            try
            {
                foreach (var field in type.GetFields(flags))
                {
                    try
                    {
                        if (field.IsSpecialName) continue;
                        string mods = GetFieldModifiers(field);
                        string fType = GetTypeName(field.FieldType);
                        string val = "";
                        if (field.IsLiteral && !field.IsInitOnly)
                        {
                            try { val = $" = {field.GetRawConstantValue()}"; } catch { }
                        }
                        info.Fields.Add($"{mods} {fType} {field.Name}{val};");
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var prop in type.GetProperties(flags))
                {
                    try
                    {
                        string pType = GetTypeName(prop.PropertyType);
                        var getMethod = prop.GetGetMethod(true);
                        var setMethod = prop.GetSetMethod(true);

                        string getStr = getMethod != null ? (getMethod.IsPublic ? "get; " : $"{GetMethodAccess(getMethod)} get; ") : "";
                        string setStr = setMethod != null ? (setMethod.IsPublic ? "set; " : $"{GetMethodAccess(setMethod)} set; ") : "";
                        string access = getMethod != null ? GetMethodAccess(getMethod) : (setMethod != null ? GetMethodAccess(setMethod) : "public");

                        info.Properties.Add($"{access} {pType} {prop.Name} {{ {getStr}{setStr}}}");
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var ev in type.GetEvents(flags))
                {
                    try
                    {
                        string eType = GetTypeName(ev.EventHandlerType);
                        info.Events.Add($"public event {eType} {ev.Name};");
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var ctor in type.GetConstructors(flags))
                {
                    try
                    {
                        string access = GetMethodAccess(ctor);
                        string paramsStr = string.Join(", ", ctor.GetParameters().Select(FormatParameter));
                        info.Constructors.Add($"{access} {info.Name}({paramsStr});");
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var method in type.GetMethods(flags))
                {
                    try
                    {
                        if (method.IsSpecialName) continue;
                        string access = GetMethodAccess(method);
                        string mods = GetMethodModifiers(method);
                        string retType = GetTypeName(method.ReturnType);
                        string paramsStr = string.Join(", ", method.GetParameters().Select(FormatParameter));
                        string genArgs = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(t => t.Name))}>" : "";

                        info.Methods.Add($"{access}{mods} {retType} {method.Name}{genArgs}({paramsStr});");
                    }
                    catch { }
                }
            }
            catch { }

            return info;
        }

        private string FormatParameter(ParameterInfo p)
        {
            string prefix = "";
            if (p.IsOut) prefix = "out ";
            else if (p.ParameterType.IsByRef) prefix = "ref ";
            else if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Any()) prefix = "params ";

            string typeName = GetTypeName(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType);
            string def = "";

            if (p.HasDefaultValue)
            {
                if (p.DefaultValue == null) def = " = null";
                else if (p.DefaultValue is string s) def = $" = \"{s}\"";
                else if (p.DefaultValue is bool b) def = $" = {(b ? "true" : "false")}";
                else def = $" = {p.DefaultValue}";
            }

            return $"{prefix}{typeName} {p.Name}{def}";
        }

        private string GetFieldModifiers(FieldInfo f)
        {
            string access = f.IsPublic ? "public" : (f.IsPrivate ? "private" : (f.IsFamily ? "protected" : "internal"));
            if (f.IsLiteral) return $"public const";
            if (f.IsStatic && f.IsInitOnly) return $"{access} static readonly";
            if (f.IsStatic) return $"{access} static";
            if (f.IsInitOnly) return $"{access} readonly";
            return access;
        }

        private string GetMethodAccess(MethodBase m)
        {
            if (m.IsPublic) return "public";
            if (m.IsPrivate) return "private";
            if (m.IsFamily) return "protected";
            if (m.IsAssembly) return "internal";
            if (m.IsFamilyOrAssembly) return "protected internal";
            return "internal";
        }

        private string GetMethodModifiers(MethodBase m)
        {
            var parts = new List<string>();
            if (m.IsStatic) parts.Add("static");
            if (m.IsAbstract) parts.Add("abstract");
            else if (m.IsVirtual && !m.IsFinal) parts.Add("virtual");
            return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
        }

        private string GetTypeName(Type t)
        {
            if (t == null) return "void";
            try
            {
                if (t == typeof(void)) return "void";
                if (t == typeof(int)) return "int";
                if (t == typeof(long)) return "long";
                if (t == typeof(short)) return "short";
                if (t == typeof(byte)) return "byte";
                if (t == typeof(bool)) return "bool";
                if (t == typeof(float)) return "float";
                if (t == typeof(double)) return "double";
                if (t == typeof(string)) return "string";
                if (t == typeof(object)) return "object";

                if (t.IsGenericType)
                {
                    string genericName = t.Name.Split('`')[0];
                    var typeArgs = string.Join(", ", t.GetGenericArguments().Select(GetTypeName));
                    return $"{genericName}<{typeArgs}>";
                }

                return t.Name;
            }
            catch
            {
                try { return t.Name; } catch { return "object"; }
            }
        }

        private void WriteIndividualFiles(string decompileDir, List<TypeSignatureInfo> infos)
        {
            if (_options.CleanDecompileFolder && Directory.Exists(decompileDir))
            {
                Directory.Delete(decompileDir, true);
            }
            Directory.CreateDirectory(decompileDir);

            // 디렉터리별로 이미 쓴 파일명 (대소문자 무시)
            var usedNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var info in infos)
            {
                string nsPath = info.Namespace == "<global>" || string.IsNullOrEmpty(info.Namespace) ? "Global" : info.Namespace.Replace('.', Path.DirectorySeparatorChar);
                string targetDir = Path.Combine(decompileDir, nsPath);
                Directory.CreateDirectory(targetDir);

                // Windows 파일 시스템은 대소문자를 구분하지 않는다. 난독화된 타입은 _fA / _FA 처럼
                // 대소문자만 다른 이름이 흔해서, 그냥 쓰면 뒤 타입이 앞 타입을 덮어써 사라진다.
                string baseName = SanitizeFileName(info.RawTypeName ?? info.Name);
                string safeFileName = baseName + ".cs";

                if (!usedNames.TryGetValue(targetDir, out var takenInDir))
                {
                    takenInDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    usedNames[targetDir] = takenInDir;
                }

                if (!takenInDir.Add(safeFileName))
                {
                    int dup = 2;
                    while (!takenInDir.Add($"{baseName}__{dup}.cs")) dup++;
                    safeFileName = $"{baseName}__{dup}.cs";
                }

                string filePath = Path.Combine(targetDir, safeFileName);

                using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    writer.WriteLine("using System;");
                    writer.WriteLine("using System.Collections.Generic;");
                    writer.WriteLine("using UnityEngine;");
                    writer.WriteLine();

                    string nsHeader = info.Namespace == "<global>" || string.IsNullOrEmpty(info.Namespace) ? "Global" : info.Namespace;
                    writer.WriteLine($"namespace {nsHeader}");
                    writer.WriteLine("{");

                    string baseClause = "";
                    var bases = new List<string>();
                    if (!string.IsNullOrEmpty(info.BaseType)) bases.Add(info.BaseType);
                    if (info.Interfaces != null && info.Interfaces.Count > 0) bases.AddRange(info.Interfaces);
                    if (bases.Count > 0) baseClause = $" : {string.Join(", ", bases)}";

                    writer.WriteLine($"    {info.AccessModifiers} {info.Kind} {info.Name}{baseClause}");
                    writer.WriteLine("    {");

                    if (info.Kind == "enum")
                    {
                        foreach (var member in info.EnumMembers)
                        {
                            writer.WriteLine($"        {member},");
                        }
                    }
                    else
                    {
                        if (info.Fields.Count > 0)
                        {
                            writer.WriteLine("        // Fields");
                            foreach (var field in info.Fields) writer.WriteLine($"        {field}");
                            writer.WriteLine();
                        }

                        if (info.Properties.Count > 0)
                        {
                            writer.WriteLine("        // Properties");
                            foreach (var prop in info.Properties) writer.WriteLine($"        {prop}");
                            writer.WriteLine();
                        }

                        if (info.Events.Count > 0)
                        {
                            writer.WriteLine("        // Events");
                            foreach (var ev in info.Events) writer.WriteLine($"        {ev}");
                            writer.WriteLine();
                        }

                        if (info.Constructors.Count > 0)
                        {
                            writer.WriteLine("        // Constructors");
                            foreach (var ctor in info.Constructors) writer.WriteLine($"        {ctor}");
                            writer.WriteLine();
                        }

                        if (info.Methods.Count > 0)
                        {
                            writer.WriteLine("        // Methods");
                            foreach (var method in info.Methods) writer.WriteLine($"        {method}");
                        }
                    }

                    writer.WriteLine("    }");
                    writer.WriteLine("}");
                }
            }
        }

        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in fileName)
            {
                if (invalidChars.Contains(c) || c == '<' || c == '>' || c == ':' || c == '*' || c == '?' || c == '"' || c == '|' || c == '/')
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private void WriteCSharpSignatures(string filePath, string asmName, List<TypeSignatureInfo> infos)
        {
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine($"// ==========================================================================");
                writer.WriteLine($"// Assembly Signature Dump for: {asmName}");
                writer.WriteLine($"// Generated on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"// Total Types: {infos.Count}");
                writer.WriteLine($"// ==========================================================================\n");

                var groups = infos.GroupBy(i => i.Namespace).OrderBy(g => g.Key);

                foreach (var group in groups)
                {
                    writer.WriteLine($"namespace {group.Key}");
                    writer.WriteLine("{");

                    foreach (var info in group)
                    {
                        string baseClause = "";
                        var bases = new List<string>();
                        if (!string.IsNullOrEmpty(info.BaseType)) bases.Add(info.BaseType);
                        if (info.Interfaces != null && info.Interfaces.Count > 0) bases.AddRange(info.Interfaces);
                        if (bases.Count > 0) baseClause = $" : {string.Join(", ", bases)}";

                        writer.WriteLine($"    {info.AccessModifiers} {info.Kind} {info.Name}{baseClause}");
                        writer.WriteLine("    {");

                        if (info.Kind == "enum")
                        {
                            foreach (var member in info.EnumMembers)
                            {
                                writer.WriteLine($"        {member},");
                            }
                        }
                        else
                        {
                            if (info.Fields.Count > 0)
                            {
                                writer.WriteLine("        // Fields");
                                foreach (var field in info.Fields) writer.WriteLine($"        {field}");
                                writer.WriteLine();
                            }

                            if (info.Properties.Count > 0)
                            {
                                writer.WriteLine("        // Properties");
                                foreach (var prop in info.Properties) writer.WriteLine($"        {prop}");
                                writer.WriteLine();
                            }

                            if (info.Events.Count > 0)
                            {
                                writer.WriteLine("        // Events");
                                foreach (var ev in info.Events) writer.WriteLine($"        {ev}");
                                writer.WriteLine();
                            }

                            if (info.Constructors.Count > 0)
                            {
                                writer.WriteLine("        // Constructors");
                                foreach (var ctor in info.Constructors) writer.WriteLine($"        {ctor}");
                                writer.WriteLine();
                            }

                            if (info.Methods.Count > 0)
                            {
                                writer.WriteLine("        // Methods");
                                foreach (var method in info.Methods) writer.WriteLine($"        {method}");
                            }
                        }

                        writer.WriteLine("    }\n");
                    }

                    writer.WriteLine("}\n");
                }
            }
        }

        private void WriteSummaryText(string filePath, string asmName, List<TypeSignatureInfo> infos)
        {
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine("==========================================================================");
                writer.WriteLine($"  ASSEMBLY SIGNATURE DUMP SUMMARY - {asmName}");
                writer.WriteLine("==========================================================================");
                writer.WriteLine($"Generated At    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"Total Types     : {infos.Count}");
                writer.WriteLine($"Classes         : {infos.Count(i => i.Kind == "class")}");
                writer.WriteLine($"Structs         : {infos.Count(i => i.Kind == "struct")}");
                writer.WriteLine($"Enums           : {infos.Count(i => i.Kind == "enum")}");
                writer.WriteLine($"Interfaces      : {infos.Count(i => i.Kind == "interface")}");
                writer.WriteLine($"Delegates       : {infos.Count(i => i.Kind == "delegate")}");
                writer.WriteLine($"Total Methods   : {infos.Sum(i => i.Methods.Count)}");
                writer.WriteLine($"Total Fields    : {infos.Sum(i => i.Fields.Count)}");
                writer.WriteLine($"Total Properties: {infos.Sum(i => i.Properties.Count)}");
                writer.WriteLine("--------------------------------------------------------------------------\n");

                writer.WriteLine("NAMESPACES SUMMARY:");
                var namespaces = infos.GroupBy(i => i.Namespace).OrderByDescending(g => g.Count());
                foreach (var ns in namespaces)
                {
                    writer.WriteLine($"  - {ns.Key,-40} : {ns.Count()} types");
                }

                writer.WriteLine("\n--------------------------------------------------------------------------");
                writer.WriteLine("ALL DUMPED TYPE NAMES:");
                writer.WriteLine("--------------------------------------------------------------------------");
                foreach (var info in infos)
                {
                    writer.WriteLine($"[{info.Kind.ToUpper(),-9}] {info.FullName}");
                }
            }
        }

        private void WriteJsonMetadata(string filePath, List<TypeSignatureInfo> infos)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < infos.Count; i++)
            {
                var item = infos[i];
                sb.AppendLine("  {");
                sb.AppendLine($"    \"Namespace\": \"{EscapeJson(item.Namespace)}\",");
                sb.AppendLine($"    \"Name\": \"{EscapeJson(item.Name)}\",");
                sb.AppendLine($"    \"FullName\": \"{EscapeJson(item.FullName)}\",");
                sb.AppendLine($"    \"Kind\": \"{EscapeJson(item.Kind)}\",");
                sb.AppendLine($"    \"AccessModifiers\": \"{EscapeJson(item.AccessModifiers)}\",");
                sb.AppendLine($"    \"BaseType\": {(item.BaseType == null ? "null" : $"\"{EscapeJson(item.BaseType)}\"")},");
                sb.AppendLine($"    \"Interfaces\": [{string.Join(", ", item.Interfaces.Select(x => $"\"{EscapeJson(x)}\""))}],");
                sb.AppendLine($"    \"EnumMembers\": [{string.Join(", ", item.EnumMembers.Select(x => $"\"{EscapeJson(x)}\""))}],");
                sb.AppendLine($"    \"FieldsCount\": {item.Fields.Count},");
                sb.AppendLine($"    \"PropertiesCount\": {item.Properties.Count},");
                sb.AppendLine($"    \"MethodsCount\": {item.Methods.Count}");
                sb.Append("  }");
                if (i < infos.Count - 1) sb.AppendLine(",");
                else sb.AppendLine();
            }
            sb.AppendLine("]");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
