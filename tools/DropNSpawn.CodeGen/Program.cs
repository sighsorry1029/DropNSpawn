using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DropNSpawn.CodeGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            CodeGenArguments options = CodeGenArguments.Parse(args);
            string projectDir = Path.GetFullPath(options.ProjectDir);
            string specPath = Path.Combine(projectDir, "codegen", "transport-schema.json");
            if (!File.Exists(specPath))
            {
                throw new FileNotFoundException("Transport schema spec was not found.", specPath);
            }

            TransportSpec spec = LoadSpec(specPath);
            Dictionary<string, string> outputs = new(StringComparer.OrdinalIgnoreCase)
            {
                [Path.Combine(projectDir, "Generated", "Transport", "GeneratedValueCodecs.generated.cs")] = RenderValueCodecs(spec),
                [Path.Combine(projectDir, "Generated", "Transport", "GeneratedEntrySchemas.generated.cs")] = RenderEntrySchemas(spec),
                [Path.Combine(projectDir, "Generated", "Transport", "GeneratedDomainCodecs.generated.cs")] = RenderDomainCodecs(spec)
            };

            bool hasChanges = false;
            foreach ((string path, string content) in outputs)
            {
                bool changed = options.Mode == CodeGenMode.Verify
                    ? VerifyFileContent(path, content)
                    : WriteFileIfChanged(path, content);
                hasChanges |= changed;
            }

            if (options.Mode == CodeGenMode.Verify && hasChanges)
            {
                Console.Error.WriteLine("Generated transport files are out of date. Run the generator in Generate mode.");
                return 1;
            }

            List<string> coverageErrors = VerifyTransportCoverage(projectDir, spec);
            if (coverageErrors.Count > 0)
            {
                Console.Error.WriteLine("Transport schema coverage audit failed:");
                foreach (string error in coverageErrors)
                {
                    Console.Error.WriteLine($"- {error}");
                }

                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static TransportSpec LoadSpec(string specPath)
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        using FileStream stream = File.OpenRead(specPath);
        return JsonSerializer.Deserialize<TransportSpec>(stream, options)
               ?? throw new InvalidOperationException("Failed to deserialize transport schema spec.");
    }

    private static string RenderValueCodecs(TransportSpec spec)
    {
        StringBuilder builder = CreateFileBuilder("Generated value codecs");
        builder.AppendLine("namespace DropNSpawn;");
        builder.AppendLine();
        builder.AppendLine("internal static partial class NetworkPayloadSyncSupport");
        builder.AppendLine("{");
        foreach (ValueCodecSpec codec in spec.ValueCodecs)
        {
            string backingFieldName = CreateBackingFieldName(codec.Name);
            builder.AppendLine($"    private static ValueCodec<{codec.TypeName}> {backingFieldName};");
            builder.AppendLine($"    private static ValueCodec<{codec.TypeName}> {codec.Name} =>");
            builder.AppendLine($"        {backingFieldName} ??=");
            builder.AppendLine("            new(");
            builder.AppendLine($"                {codec.SignatureWriterExpression},");
            builder.AppendLine($"                {codec.PayloadWriterExpression},");
            builder.AppendLine($"                {codec.PayloadReaderExpression},");
            builder.AppendLine($"                {codec.CloneExpression});");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string RenderEntrySchemas(TransportSpec spec)
    {
        StringBuilder builder = CreateFileBuilder("Generated entry transport schemas");
        builder.AppendLine("namespace DropNSpawn;");
        builder.AppendLine();
        builder.AppendLine("internal static partial class NetworkPayloadSyncSupport");
        builder.AppendLine("{");
        foreach (EntrySchemaSpec schema in spec.EntrySchemas)
        {
            builder.AppendLine($"    private static EntryTransportSchema<{schema.EntryTypeName}> {schema.MethodName}()");
            builder.AppendLine("    {");
            builder.AppendLine("        return new(");
            builder.AppendLine($"            {schema.DtoVersionExpression},");
            builder.AppendLine($"            {schema.CreateEntryExpression},");
            for (int index = 0; index < schema.Fields.Count; index++)
            {
                string suffix = index == schema.Fields.Count - 1 ? string.Empty : ",";
                builder.AppendLine($"            {schema.Fields[index].Expression}{suffix}");
            }

            builder.AppendLine("        );");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string RenderDomainCodecs(TransportSpec spec)
    {
        StringBuilder builder = CreateFileBuilder("Generated domain codecs");
        builder.AppendLine("namespace DropNSpawn;");
        builder.AppendLine();
        builder.AppendLine("internal static partial class NetworkPayloadSyncSupport");
        builder.AppendLine("{");
        foreach (EntrySchemaSpec schema in spec.EntrySchemas)
        {
            string codecName = CreateDomainCodecName(schema.MethodName);
            string backingFieldName = CreateBackingFieldName(codecName);
            builder.AppendLine($"    private static DomainCodec<{schema.EntryTypeName}> {backingFieldName};");
            builder.AppendLine($"    private static DomainCodec<{schema.EntryTypeName}> {codecName} =>");
            builder.AppendLine($"        {backingFieldName} ??= new(Create{codecName[..^"Codec".Length]}EntrySchema());");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static StringBuilder CreateFileBuilder(string description)
    {
        StringBuilder builder = new();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine($"// {description}. Do not edit by hand.");
        builder.AppendLine();
        return builder;
    }

    private static string CreateBackingFieldName(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            throw new ArgumentException("Value codec name must not be empty.", nameof(propertyName));
        }

        return "_" + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
    }

    private static string CreateDomainCodecName(string schemaMethodName)
    {
        Match match = Regex.Match(schemaMethodName ?? "", @"^Create(?<domain>[A-Z][A-Za-z0-9_]*)EntrySchema$");
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Entry schema method '{schemaMethodName}' must match 'Create{{Domain}}EntrySchema' to generate a domain codec.");
        }

        return match.Groups["domain"].Value + "Codec";
    }

    private static bool WriteFileIfChanged(string path, string content)
    {
        string normalizedContent = NormalizeContent(content);
        string directoryPath = Path.GetDirectoryName(path)
                               ?? throw new InvalidOperationException($"Generated output path '{path}' does not have a directory.");
        Directory.CreateDirectory(directoryPath);

        if (File.Exists(path))
        {
            string existingContent = NormalizeContent(File.ReadAllText(path));
            if (string.Equals(existingContent, normalizedContent, StringComparison.Ordinal))
            {
                return false;
            }
        }

        File.WriteAllText(path, normalizedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static bool VerifyFileContent(string path, string content)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        string existingContent = NormalizeContent(File.ReadAllText(path));
        string expectedContent = NormalizeContent(content);
        return !string.Equals(existingContent, expectedContent, StringComparison.Ordinal);
    }

    private static string NormalizeContent(string content)
    {
        string normalized = content.Replace("\r\n", "\n");
        if (!normalized.EndsWith("\n", StringComparison.Ordinal))
        {
            normalized += "\n";
        }

        return normalized.Replace("\n", Environment.NewLine);
    }

    private static List<string> VerifyTransportCoverage(string projectDir, TransportSpec spec)
    {
        SourceIndex sourceIndex = SourceIndex.Load(projectDir);
        List<string> errors = new();
        VerifyEntrySchemaCoverage(sourceIndex, spec, errors);
        VerifyValueCodecCoverage(sourceIndex, spec, errors);
        return errors;
    }

    private static void VerifyEntrySchemaCoverage(SourceIndex sourceIndex, TransportSpec spec, List<string> errors)
    {
        foreach (EntrySchemaSpec schema in spec.EntrySchemas)
        {
            List<string> properties = sourceIndex.FindPublicSettableProperties(schema.EntryTypeName);
            foreach (string property in properties)
            {
                int matchingFieldCount = schema.Fields.Count(field => ContainsIdentifier(field.Expression, property));
                if (matchingFieldCount == 0)
                {
                    errors.Add(
                        $"Entry schema '{schema.MethodName}' for '{schema.EntryTypeName}' does not include property '{property}'.");
                }
                else if (matchingFieldCount > 1)
                {
                    errors.Add(
                        $"Entry schema '{schema.MethodName}' for '{schema.EntryTypeName}' includes property '{property}' {matchingFieldCount} times.");
                }
            }
        }
    }

    private static void VerifyValueCodecCoverage(SourceIndex sourceIndex, TransportSpec spec, List<string> errors)
    {
        foreach (ValueCodecSpec codec in spec.ValueCodecs)
        {
            CoverageAuditSpec audit = CreateValueCodecAudit(codec);
            VerifyCoverageAudit(sourceIndex, audit, errors);
        }

        foreach (CoverageAuditSpec audit in spec.CoverageAudits)
        {
            VerifyCoverageAudit(sourceIndex, audit, errors);
        }
    }

    private static void VerifyCoverageAudit(SourceIndex sourceIndex, CoverageAuditSpec audit, List<string> errors)
    {
        List<string> properties = sourceIndex.FindPublicSettableProperties(audit.TypeName)
            .Where(property => !audit.IgnoredProperties.Contains(property, StringComparer.Ordinal))
            .ToList();
        if (properties.Count == 0)
        {
            errors.Add($"Coverage audit for '{audit.TypeName}' did not find any public settable properties.");
            return;
        }

        foreach (CoverageAuditMethodSpec method in audit.Methods)
        {
            string? body = sourceIndex.FindExpandedMethodBody(method.Name, method.File, method.ParametersContain);
            if (body == null)
            {
                errors.Add(
                    $"Coverage audit for '{audit.TypeName}' could not find method '{method.Name}'" +
                    FormatOptionalQualifier(method.File, method.ParametersContain) + ".");
                continue;
            }

            foreach (string property in properties)
            {
                if (!method.IgnoredProperties.Contains(property, StringComparer.Ordinal) &&
                    !ContainsPropertyReference(body, property))
                {
                    errors.Add(
                        $"Coverage audit for '{audit.TypeName}' method '{method.Name}'" +
                        FormatOptionalQualifier(method.File, method.ParametersContain) +
                        $" does not reference property '{property}'.");
                }
            }
        }
    }

    private static CoverageAuditSpec CreateValueCodecAudit(ValueCodecSpec codec)
    {
        return new CoverageAuditSpec
        {
            TypeName = codec.TypeName,
            IgnoredProperties = new List<string>(codec.CoverageIgnoredProperties),
            Methods =
            {
                new CoverageAuditMethodSpec
                {
                    Name = ExtractMethodName(codec.CloneExpression),
                    IgnoredProperties = new List<string>(codec.CloneCoverageIgnoredProperties)
                },
                new CoverageAuditMethodSpec
                {
                    Name = ExtractMethodName(codec.SignatureWriterExpression),
                    ParametersContain = "PayloadSignatureBuilder",
                    IgnoredProperties = new List<string>(codec.SignatureCoverageIgnoredProperties)
                },
                new CoverageAuditMethodSpec
                {
                    Name = ExtractMethodName(codec.PayloadWriterExpression),
                    ParametersContain = "ZPackage",
                    IgnoredProperties = new List<string>(codec.PayloadCoverageIgnoredProperties)
                },
                new CoverageAuditMethodSpec
                {
                    Name = ExtractMethodName(codec.PayloadReaderExpression),
                    IgnoredProperties = new List<string>(codec.PayloadCoverageIgnoredProperties)
                }
            }
        };
    }

    private static string ExtractMethodName(string expression)
    {
        MatchCollection matches = Regex.Matches(expression ?? "", @"\b(?<name>[A-Z][A-Za-z0-9_]*)\s*(?:\(|$)");
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"Could not find a method name in expression '{expression}'.");
        }

        return matches[^1].Groups["name"].Value;
    }

    private static string FormatOptionalQualifier(string? file, string? parametersContain)
    {
        List<string> qualifiers = new();
        if (!string.IsNullOrWhiteSpace(file))
        {
            qualifiers.Add($"file '{file}'");
        }

        if (!string.IsNullOrWhiteSpace(parametersContain))
        {
            qualifiers.Add($"parameters containing '{parametersContain}'");
        }

        return qualifiers.Count == 0 ? "" : $" ({string.Join(", ", qualifiers)})";
    }

    private static bool ContainsIdentifier(string text, string identifier)
    {
        return Regex.IsMatch(text, $@"\b{Regex.Escape(identifier)}\b");
    }

    private static bool ContainsPropertyReference(string text, string propertyName)
    {
        return Regex.IsMatch(text, $@"(?:\.|\b){Regex.Escape(propertyName)}\b");
    }

    private sealed class SourceIndex
    {
        private readonly List<SourceFile> _files;
        private readonly Dictionary<string, TypeInfo> _typesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _propertiesByTypeName = new(StringComparer.Ordinal);

        private SourceIndex(List<SourceFile> files)
        {
            _files = files;
        }

        public static SourceIndex Load(string projectDir)
        {
            string projectPath = Path.Combine(projectDir, "DropNSpawn.csproj");
            if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException("DropNSpawn project file was not found.", projectPath);
            }

            XDocument project = XDocument.Load(projectPath);
            XNamespace ns = project.Root?.Name.Namespace ?? XNamespace.None;
            List<SourceFile> files = project.Descendants(ns + "Compile")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.Combine(projectDir, include!.Replace('\\', Path.DirectorySeparatorChar)))
                .Where(File.Exists)
                .Select(path => new SourceFile(path, File.ReadAllText(path)))
                .ToList();

            SourceIndex index = new(files);
            index.IndexTypes();
            return index;
        }

        public List<string> FindPublicSettableProperties(string typeName)
        {
            if (_propertiesByTypeName.TryGetValue(typeName, out List<string>? cached))
            {
                return cached;
            }

            if (!_typesByName.TryGetValue(typeName, out TypeInfo? type))
            {
                throw new InvalidOperationException($"Type '{typeName}' was not found in compiled source files.");
            }

            List<string> properties = new();
            if (!string.IsNullOrWhiteSpace(type.BaseTypeName) &&
                _typesByName.ContainsKey(type.BaseTypeName))
            {
                properties.AddRange(FindPublicSettableProperties(type.BaseTypeName));
            }

            MatchCollection matches = Regex.Matches(
                type.Body,
                @"\bpublic\s+[^;\r\n{}=]+?\s+(?<name>[A-Z][A-Za-z0-9_]*)\s*\{\s*get;\s*set;",
                RegexOptions.Multiline);
            foreach (Match match in matches)
            {
                if (HasYamlIgnoreAttribute(type.Body, match.Index))
                {
                    continue;
                }

                string property = match.Groups["name"].Value;
                if (!properties.Contains(property, StringComparer.Ordinal))
                {
                    properties.Add(property);
                }
            }

            _propertiesByTypeName[typeName] = properties;
            return properties;
        }

        private static bool HasYamlIgnoreAttribute(string body, int propertyIndex)
        {
            int scanStart = Math.Max(0, propertyIndex - 256);
            string prefix = body.Substring(scanStart, propertyIndex - scanStart);
            return Regex.IsMatch(prefix, @"\[YamlIgnore\]\s*$", RegexOptions.Multiline);
        }

        public string? FindExpandedMethodBody(string methodName, string? fileName, string? parametersContain)
        {
            return FindExpandedMethodBody(methodName, fileName, parametersContain, new HashSet<string>(StringComparer.Ordinal), depth: 0);
        }

        private string? FindExpandedMethodBody(
            string methodName,
            string? fileName,
            string? parametersContain,
            HashSet<string> visitedMethods,
            int depth)
        {
            MethodMatch? method = FindMethod(methodName, fileName, parametersContain);
            if (method == null)
            {
                return null;
            }

            if (depth >= 3 || !visitedMethods.Add(method.SignatureKey))
            {
                return method.Body;
            }

            StringBuilder builder = new(method.Body);
            foreach (string calledMethodName in EnumerateCalledHelperMethods(method.Body))
            {
                string? calledBody = FindExpandedMethodBody(calledMethodName, Path.GetFileName(method.Path), null, visitedMethods, depth + 1);
                if (calledBody != null)
                {
                    builder.AppendLine();
                    builder.AppendLine(calledBody);
                }
            }

            return builder.ToString();
        }

        private MethodMatch? FindMethod(string methodName, string? fileName, string? parametersContain)
        {
            IEnumerable<SourceFile> candidateFiles = _files;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidateFiles = candidateFiles.Where(file =>
                    string.Equals(Path.GetFileName(file.Path), fileName, StringComparison.OrdinalIgnoreCase));
            }

            foreach (SourceFile file in candidateFiles)
            {
                foreach (Match match in Regex.Matches(file.Content, $@"\b{Regex.Escape(methodName)}\s*\("))
                {
                    int lineStart = file.Content.LastIndexOf('\n', match.Index);
                    lineStart = lineStart < 0 ? 0 : lineStart + 1;
                    string declarationPrefix = file.Content[lineStart..match.Index];
                    if (!LooksLikeMethodDeclaration(declarationPrefix))
                    {
                        continue;
                    }

                    int openBrace = file.Content.IndexOf('{', match.Index);
                    if (openBrace < 0)
                    {
                        continue;
                    }

                    int semicolon = file.Content.IndexOf(';', match.Index);
                    if (semicolon >= 0 && semicolon < openBrace)
                    {
                        continue;
                    }

                    string signature = file.Content[match.Index..openBrace];
                    if (!string.IsNullOrWhiteSpace(parametersContain) &&
                        signature.IndexOf(parametersContain, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    int closeBrace = FindMatchingBrace(file.Content, openBrace);
                    if (closeBrace < 0)
                    {
                        continue;
                    }

                    return new MethodMatch(
                        file.Path,
                        $"{file.Path}:{match.Index}",
                        signature,
                        file.Content[(openBrace + 1)..closeBrace]);
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateCalledHelperMethods(string body)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(body, @"\b(?<name>(?:Copy|Write|Read|Clone)[A-Z][A-Za-z0-9_]*)\s*\("))
            {
                string name = match.Groups["name"].Value;
                if (name is "WriteOptional" or "WriteList" or "WriteStringList" or "WriteStringDictionary" or
                    "WriteNullableString" or "WriteNullableInt" or "WriteNullableFloat" or "WriteNullableBool" or
                    "ReadOptional" or "ReadList" or "ReadStringList" or "ReadStringDictionary" or
                    "ReadNullableString" or "ReadNullableInt" or "ReadNullableFloat" or "ReadNullableBool")
                {
                    continue;
                }

                names.Add(name);
            }

            return names;
        }

        private void IndexTypes()
        {
            foreach (SourceFile file in _files)
            {
                foreach (Match match in Regex.Matches(
                             file.Content,
                             @"\b(?:class|struct)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<suffix>[^{;]*)\{"))
                {
                    string name = match.Groups["name"].Value;
                    int openBrace = file.Content.IndexOf('{', match.Index + match.Length - 1);
                    if (openBrace < 0)
                    {
                        continue;
                    }

                    int closeBrace = FindMatchingBrace(file.Content, openBrace);
                    if (closeBrace < 0)
                    {
                        continue;
                    }

                    string baseType = ParseBaseTypeName(match.Groups["suffix"].Value);
                    _typesByName[name] = new TypeInfo(
                        name,
                        baseType,
                        file.Content[(openBrace + 1)..closeBrace]);
                }
            }
        }

        private static bool LooksLikeMethodDeclaration(string declarationPrefix)
        {
            return declarationPrefix.IndexOf("=>", StringComparison.Ordinal) < 0 &&
                   declarationPrefix.IndexOf('.', StringComparison.Ordinal) < 0 &&
                   (declarationPrefix.IndexOf("private", StringComparison.Ordinal) >= 0 ||
                    declarationPrefix.IndexOf("internal", StringComparison.Ordinal) >= 0 ||
                    declarationPrefix.IndexOf("public", StringComparison.Ordinal) >= 0);
        }

        private static string ParseBaseTypeName(string suffix)
        {
            int colonIndex = suffix.IndexOf(':');
            if (colonIndex < 0)
            {
                return "";
            }

            string baseList = suffix[(colonIndex + 1)..];
            foreach (string rawPart in baseList.Split(','))
            {
                string part = rawPart.Trim();
                if (part.Length == 0 || part.StartsWith("I", StringComparison.Ordinal))
                {
                    continue;
                }

                Match match = Regex.Match(part, @"^[A-Za-z_][A-Za-z0-9_]*");
                if (match.Success)
                {
                    return match.Value;
                }
            }

            return "";
        }

        private static int FindMatchingBrace(string text, int openBrace)
        {
            int depth = 0;
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;
            bool inVerbatimString = false;
            bool inChar = false;

            for (int index = openBrace; index < text.Length; index++)
            {
                char current = text[index];
                char next = index + 1 < text.Length ? text[index + 1] : '\0';

                if (inLineComment)
                {
                    if (current == '\n')
                    {
                        inLineComment = false;
                    }

                    continue;
                }

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        index++;
                    }

                    continue;
                }

                if (inString)
                {
                    if (inVerbatimString && current == '"' && next == '"')
                    {
                        index++;
                        continue;
                    }

                    if (current == '"' && (!IsEscaped(text, index) || inVerbatimString))
                    {
                        inString = false;
                        inVerbatimString = false;
                    }

                    continue;
                }

                if (inChar)
                {
                    if (current == '\'' && !IsEscaped(text, index))
                    {
                        inChar = false;
                    }

                    continue;
                }

                if (current == '/' && next == '/')
                {
                    inLineComment = true;
                    index++;
                    continue;
                }

                if (current == '/' && next == '*')
                {
                    inBlockComment = true;
                    index++;
                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    inVerbatimString = index > 0 && text[index - 1] == '@';
                    continue;
                }

                if (current == '\'')
                {
                    inChar = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                    continue;
                }

                if (current == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }
                }
            }

            return -1;
        }

        private static bool IsEscaped(string text, int index)
        {
            int slashCount = 0;
            for (int current = index - 1; current >= 0 && text[current] == '\\'; current--)
            {
                slashCount++;
            }

            return slashCount % 2 != 0;
        }
    }

    private sealed record SourceFile(string Path, string Content);

    private sealed record TypeInfo(string Name, string BaseTypeName, string Body);

    private sealed record MethodMatch(string Path, string SignatureKey, string Signature, string Body);

    private sealed class CodeGenArguments
    {
        public string ProjectDir { get; private set; } = ".";
        public CodeGenMode Mode { get; private set; } = CodeGenMode.Generate;

        public static CodeGenArguments Parse(string[] args)
        {
            CodeGenArguments parsed = new();
            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                switch (argument)
                {
                    case "--project-dir":
                        parsed.ProjectDir = RequireValue(args, ref index, argument);
                        break;
                    case "--mode":
                        string modeValue = RequireValue(args, ref index, argument);
                        parsed.Mode = Enum.TryParse<CodeGenMode>(modeValue, ignoreCase: true, out CodeGenMode mode)
                            ? mode
                            : throw new ArgumentOutOfRangeException(nameof(args), modeValue, "Unsupported code generation mode.");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(args), argument, "Unsupported argument.");
                }
            }

            return parsed;
        }

        private static string RequireValue(string[] args, ref int index, string argumentName)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Argument '{argumentName}' requires a value.", nameof(args));
            }

            index++;
            return args[index];
        }
    }

    private enum CodeGenMode
    {
        Generate,
        Verify
    }

    private sealed class TransportSpec
    {
        public List<ValueCodecSpec> ValueCodecs { get; set; } = new();
        public List<EntrySchemaSpec> EntrySchemas { get; set; } = new();
        public List<CoverageAuditSpec> CoverageAudits { get; set; } = new();
    }

    private sealed class ValueCodecSpec
    {
        public string Name { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string SignatureWriterExpression { get; set; } = "";
        public string PayloadWriterExpression { get; set; } = "";
        public string PayloadReaderExpression { get; set; } = "";
        public string CloneExpression { get; set; } = "";
        public List<string> CoverageIgnoredProperties { get; set; } = new();
        public List<string> CloneCoverageIgnoredProperties { get; set; } = new();
        public List<string> SignatureCoverageIgnoredProperties { get; set; } = new();
        public List<string> PayloadCoverageIgnoredProperties { get; set; } = new();
    }

    private sealed class EntrySchemaSpec
    {
        public string MethodName { get; set; } = "";
        public string EntryTypeName { get; set; } = "";
        public string DtoVersionExpression { get; set; } = "";
        public string CreateEntryExpression { get; set; } = "";
        public List<EntryFieldSpec> Fields { get; set; } = new();
    }

    private sealed class EntryFieldSpec
    {
        public string Expression { get; set; } = "";
    }

    private sealed class CoverageAuditSpec
    {
        public string TypeName { get; set; } = "";
        public List<string> IgnoredProperties { get; set; } = new();
        public List<CoverageAuditMethodSpec> Methods { get; set; } = new();
    }

    private sealed class CoverageAuditMethodSpec
    {
        public string Name { get; set; } = "";
        public string? File { get; set; }
        public string? ParametersContain { get; set; }
        public List<string> IgnoredProperties { get; set; } = new();
    }
}
