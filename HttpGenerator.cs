using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

#nullable enable

namespace PlatformExtensions
{
    [Generator]
    public partial class HttpGenerator : ISourceGenerator
    {
        public void Execute(GeneratorExecutionContext context)
        {
            // add the attribute text
            context.AddSource("Utilities", SourceText.From(utilitiesText, Encoding.UTF8));
            context.AddSource("RouteAttribute", SourceText.From(routeAttributeText, Encoding.UTF8));
            context.AddSource("MustacheViewAttribute", SourceText.From(mustacheViewAttributeText, Encoding.UTF8));

            var assembly = Assembly.GetExecutingAssembly();

            var fileName = "Ben.AspNetCore.PlatformExtensions.BufferWriter.cs";
            var stream = assembly.GetManifestResourceStream(fileName);
            if (stream == null) throw new FileNotFoundException("Cannot find mappings file.", fileName);
            context.AddSource(fileName, SourceText.From(stream, Encoding.UTF8, canBeEmbedded: true));

            fileName = "Ben.AspNetCore.PlatformExtensions.BufferExtensions.cs";
            stream = assembly.GetManifestResourceStream(fileName);
            if (stream == null) throw new FileNotFoundException("Cannot find mappings file.", fileName);
            context.AddSource(fileName, SourceText.From(stream, Encoding.UTF8, canBeEmbedded: true));

            fileName = "Ben.AspNetCore.PlatformExtensions.IHttpConnection.cs";
            stream = assembly.GetManifestResourceStream(fileName);
            if (stream == null) throw new FileNotFoundException("Cannot find mappings file.", fileName);
            context.AddSource(fileName, SourceText.From(stream, Encoding.UTF8, canBeEmbedded: true));

            fileName = "Ben.AspNetCore.PlatformExtensions.HttpApplication.cs";
            stream = assembly.GetManifestResourceStream(fileName);
            if (stream == null) throw new FileNotFoundException("Cannot find mappings file.", fileName);
            context.AddSource(fileName, SourceText.From(stream, Encoding.UTF8, canBeEmbedded: true));

            // retreive the populated receiver 
            if (context.SyntaxReceiver is not SyntaxReceiver receiver)
                return;

            // we're going to create a new compilation that contains the attribute.
            // TODO: we should allow source generators to provide source during initialize, so that this step isn't required.
            CSharpParseOptions options = (context.Compilation as CSharpCompilation).SyntaxTrees[0].Options as CSharpParseOptions;

            ProcessRoute(context, receiver, options);
        }

        private void ProcessRoute(GeneratorExecutionContext context, SyntaxReceiver receiver, CSharpParseOptions options)
        {
            Compilation routeAttributeCompilation = context.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(SourceText.From(routeAttributeText, Encoding.UTF8), options));

            INamedTypeSymbol routeAttributeSymbol = routeAttributeCompilation.GetTypeByMetadataName("PlatformExtensions.RouteAttribute");

            // loop over the candidate fields, and keep the ones that are actually annotated
            List<(IMethodSymbol symbol, MethodDeclarationSyntax method)> routeAttributeSymbols = new ();
            bool isAsync = false;
            foreach (MethodDeclarationSyntax method in receiver.CandidateMethods)
            {
                SemanticModel routeModel = routeAttributeCompilation.GetSemanticModel(method.SyntaxTree);

                // Get the symbol being decleared by the field, and keep it if its annotated
                IMethodSymbol methodSymbol = routeModel.GetDeclaredSymbol(method);
                if (methodSymbol.GetAttributes().Any(ad => ad.AttributeClass.Equals(routeAttributeSymbol, SymbolEqualityComparer.Default)))
                {
                    routeAttributeSymbols.Add((methodSymbol, method));
                    if (!isAsync && method.ReturnType is GenericNameSyntax rt)
                    {
                        isAsync = rt.Identifier.ValueText == "Task";
                    }
                }
            }

            IEnumerable<IGrouping<INamedTypeSymbol, (IMethodSymbol symbol, MethodDeclarationSyntax method)>> classes = routeAttributeSymbols.GroupBy(f => f.symbol.ContainingType);
            if (classes.Count() != 1)
            {
                throw new Exception("One and only one application class should be defined");
            }

            var item = classes.FirstOrDefault();
            INamedTypeSymbol classSymbol = item.Key;
            string namespaceName = classSymbol.ContainingNamespace.ToDisplayString();

            var assembly = Assembly.GetExecutingAssembly();

            var fileName = "Ben.AspNetCore.PlatformExtensions.Application.cs";
            var stream = assembly.GetManifestResourceStream(fileName);
            if (stream == null) throw new FileNotFoundException("Cannot find mappings file.", fileName);

            // begin building the generated source
            StringBuilder source = new StringBuilder($@"
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using PlatformExtensions;

namespace {namespaceName}
{{
    public partial class {classSymbol.Name} : IHttpConnection
    {{
");
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                source.Append(reader.ReadToEnd());
            }

            var snippetFileName = $"Ben.AspNetCore.PlatformExtensions.Application.{(isAsync? "Async" : "Sync")}.cs";
            stream = assembly.GetManifestResourceStream(snippetFileName);
            if (stream == null) throw new FileNotFoundException("Cannot find mappings file.", snippetFileName);

            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                source.Append(reader.ReadToEnd());
            }

            if (!isAsync)
            {
                source.Append(@"
        private void ProcessRequest(ref BufferWriter<WriterAdapter> writer)
        {
            var requestType = _requestType;
");
                foreach (var group in classes)
                {
                    var isFirst = true;
                    foreach (var methodData in group)
                    {
                        source.Append(@$"
            {(!isFirst ? "else " : "")}if (requestType == RequestType.{methodData.symbol.Name})
            {{
                {methodData.symbol.Name}_Routed(ref writer{(methodData.symbol.ReturnType.SpecialType == SpecialType.System_String ? "" : ", Writer")}{(methodData.method.ParameterList.Parameters.Count > 0 ? ", _queries" : "")});
            }}");
                        isFirst = false;
                    }
                }

                source.Append(@"
            else
            {
                Default_Routed(ref writer);
            }
        }
");
            }
            else
            {
                source.Append(@"
        private Task ProcessRequestAsync() => _requestType switch
        {
");
                foreach (var group in classes)
                {
                    foreach (var methodData in group)
                    {
                        source.Append(@$"
            RequestType.{methodData.symbol.Name} => {methodData.symbol.Name}_Routed(Writer{(methodData.method.ParameterList.Parameters.Count > 0 ? ", _queries" : "")}),");
                    }
                }

                source.Append(@"
            _ => Default_Routed_Task(Writer)
        };
");
            }

            source.Append( @"
    }
}
");

            context.AddSource(fileName, SourceText.From(source.ToString(), Encoding.UTF8));

            // group the fields by class, and generate the source
            foreach (var group in classes)
            {
                string classSource = ProcessRouteClass(group.Key, group.ToList(), isAsync, routeAttributeSymbol, context);
                context.AddSource($"{group.Key.Name}.Routes.cs", SourceText.From(classSource, Encoding.UTF8));
            }

        }

        private string ProcessRouteClass(INamedTypeSymbol classSymbol, List<(IMethodSymbol symbol, MethodDeclarationSyntax method)> methods, bool isAsync, ISymbol attributeSymbol, GeneratorExecutionContext context)
        {
            if (!classSymbol.ContainingSymbol.Equals(classSymbol.ContainingNamespace, SymbolEqualityComparer.Default))
            {
                return null; //TODO: issue a diagnostic that it must be top level
            }

            string namespaceName = classSymbol.ContainingNamespace.ToDisplayString();

            // begin building the generated source
            StringBuilder source = new StringBuilder($@"
using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using PlatformExtensions;

namespace {namespaceName}
{{
    public partial class {classSymbol.Name}
    {{
        private RequestType _requestType;
        private int _queries;

        public void OnStartLine(HttpVersionAndMethod versionAndMethod, TargetOffsetPathLength targetPath, Span<byte> startLine)
        {{
            _requestType = versionAndMethod.Method == HttpMethod.Get ? GetRequestType(startLine.Slice(targetPath.Offset, targetPath.Length), ref _queries) : RequestType.Default;
        }}

        public static class Paths
        {{
");
            List<(string path, string method, bool hasQuery)> paths = new ();
            foreach ((IMethodSymbol methodSymbol, MethodDeclarationSyntax method) in methods)
            {
                AttributeData attributeData = methodSymbol.GetAttributes().Single(ad => ad.AttributeClass.Equals(attributeSymbol, SymbolEqualityComparer.Default));

                var hasQuery = false;
                var path = (string)attributeData.ConstructorArguments[0].Value;
                if (attributeData.NamedArguments.Any(kvp => kvp.Key == "Path"))
                {
                    path = attributeData.NamedArguments.SingleOrDefault(kvp => kvp.Key == "Path").Value.ToString();
                }

                paths.Add((path, methodSymbol.Name, path.IndexOf('{') >= 0));
            }

            foreach ((string path, string method, bool hasQuery) in paths)
            {
                source.Append($@"
            public static ReadOnlySpan<byte> {method} => new byte[] {{");
                var query = path.IndexOf('{');
                if (query >= 0)
                {
                    AddBytes(source, Encoding.UTF8.GetBytes(path.Substring(0, query)));
                }
                else
                {
                    AddBytes(source, Encoding.UTF8.GetBytes(path));
                }

                source.Append("};");
            }

            source.Append($@"
            public static string[] Enabled {{ get; }} = new string[] {{");
            foreach ((string path, string method, bool hasQuery) in paths)
            {
                var query = path.IndexOf('{');
                if (query >= 0)
                {
                    source.Append($"\"{path.Substring(0, query)}\", ");
                }
                else
                {
                    source.Append($"\"{path}\", ");
                }

            }
            source.Append("};");

            source.Append(@"
        }");

        if (paths.Any(pathData => pathData.hasQuery))
        {
            source.Append(@"
        private static int ParseQueries(ReadOnlySpan<byte> parameter)
        {
            if (!Utf8Parser.TryParse(parameter, out int queries, out _) || queries < 1)
            {
                queries = 1;
            }
            else if (queries > 500)
            {
                queries = 500;
            }

            return queries;
        }
");
        }

            source.Append($@"
        private RequestType GetRequestType(ReadOnlySpan<byte> path, ref int queries)
        {{
");
            var sortedPaths = paths.OrderByDescending((pathData) => pathData.path.Length * (pathData.hasQuery ? -1 : 1));

            foreach ((string path, string method, bool hasQuery) in sortedPaths)
            {
                if (!hasQuery)
                {
                    source.AppendLine($@"
            if (path.Length == {path.Length} && path.SequenceEqual(Paths.{method}))
            {{
                return RequestType.{method};
            }}");
                }
                else
                {
                    var query = path.IndexOf('{');
                    source.AppendLine($@"
            if (path.Length >= {query} && path[1] == '{path[0]}' && path.StartsWith(Paths.{method}))
            {{
                queries = ParseQueries(path.Slice({query}));
                return RequestType.{method};
            }}");
                }
            }
            source.Append(@"
            return RequestType.Default;
        }");

            source.Append($@"
        private enum RequestType
        {{
            Default,
");
            foreach ((string path, string method, bool hasQuery) in paths)
            {
                source.AppendLine($@"
            {method},");
            }
            source.Append(@"
        }");

            // create properties for each field 
            foreach ((IMethodSymbol methodSymbol, MethodDeclarationSyntax method) in methods)
            {
                ProcessRouteMethod(source, methodSymbol, method, attributeSymbol, isAsync);
            }

            AddDefaultRoutes(source);

            source.Append(@"
    } 
}");
            return source.ToString();
        }

        private void ProcessRouteMethod(StringBuilder source, IMethodSymbol methodSymbol, MethodDeclarationSyntax method, ISymbol attributeSymbol, bool isAsync)
        {
            // get the name and type of the field
            string methodName = methodSymbol.Name;
            ITypeSymbol returnType = methodSymbol.ReturnType;

            // get the AutoNotify attribute from the field, and any associated data
            AttributeData attributeData = methodSymbol.GetAttributes().Single(ad => ad.AttributeClass.Equals(attributeSymbol, SymbolEqualityComparer.Default));

            if (returnType.SpecialType == SpecialType.System_String)
            {
                TypedConstant overridenContentType = attributeData.NamedArguments.SingleOrDefault(kvp => kvp.Key == "ContentType").Value;
                string contentType = overridenContentType.Value?.ToString() ?? "text/plain";

                OutputStringMethod(source, method, methodName, contentType, isAsync);
            }
            else
            {
                TypedConstant overridenContentType = attributeData.NamedArguments.SingleOrDefault(kvp => kvp.Key == "ContentType").Value;
                string contentType = overridenContentType.Value?.ToString() ?? "application/json";

                OutputObjectMethod(source, method, methodName, contentType, isAsync);
            }
        }

        private static void OutputObjectMethod(StringBuilder source, MethodDeclarationSyntax method, string methodName, string contentType, bool isAsync)
        {
            var exp = method.ExpressionBody;
            if (exp.Expression is ObjectCreationExpressionSyntax objectCreation)
            {
                OutputSimpleJsonMethod(source, method, methodName, contentType, objectCreation.ToString(), isAsync);
                return;
            }

            var invoke = exp.Expression as InvocationExpressionSyntax;
            var rt = (method.ReturnType as GenericNameSyntax).TypeArgumentList.Arguments[0];

            var parameters = method.ParameterList.Parameters;
            var extraParams = "";
            if (parameters.Count > 0)
            {
                foreach (var item in parameters)
                {
                    extraParams = ", " + item.ToString();
                }
            }

            var type = rt.ToString();

            source.Append($@"
        private static async Task {methodName}_Routed(PipeWriter pipeWriter{extraParams})
        {{
            {methodName}_Routed_Output(pipeWriter, await {invoke.ToString()});
        }}
");
            source.Append($@"
        private static void {methodName}_Routed_Output(PipeWriter pipeWriter, {type} data)
        {{
            var writer = GetWriter(pipeWriter, sizeHint: 120{(type.Contains('[') ? " * data.Length" : "")});

            ReadOnlySpan<byte> preamble = new byte[] {{");
            AddBytes(source, Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Server: B\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Content-Type: "));
            AddBytes(source, Encoding.UTF8.GetBytes(contentType));
            AddBytes(source, Encoding.UTF8.GetBytes("\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Content-Length: "));
            source.Append($@" }};

            writer.Write(preamble);

            var lengthWriter = writer;
            ReadOnlySpan<byte> contentLengthGap = new byte[] {{");
            AddBytes(source, Encoding.UTF8.GetBytes(new string(' ', 4)));
            source.Append($@" }};
            writer.Write(contentLengthGap);

            // Date header
            writer.Write(Utilities.DateHeaderBytes);

            writer.Commit();

            Utf8JsonWriter utf8JsonWriter = Utilities.GetJsonWriter(pipeWriter);
            // Body
            JsonSerializer.Serialize(utf8JsonWriter, data, Utilities.SerializerOptions);

            // Content-Length
            lengthWriter.WriteNumeric((uint)utf8JsonWriter.BytesCommitted);
        }}
");
        }
        private static void OutputSimpleJsonMethod(StringBuilder source, MethodDeclarationSyntax method, string methodName, string contentType, string data, bool isAsync)
        {
            if (isAsync)
            {
                source.Append($@"
        private static Task {methodName}_Routed(PipeWriter pipeWriter)
        {{
            var writer = GetWriter(pipeWriter, sizeHint: 85 + Utilities.DateHeaderBytes.Length);
            {methodName}_Routed_Task(ref writer, pipeWriter);
            writer.Commit();
            return Task.CompletedTask;
        }}");
            }

            source.Append($@"
        private static void {methodName}_Routed{(isAsync ? "_Task" : "")}(ref BufferWriter<WriterAdapter> writer, PipeWriter pipeWriter)
        {{
            ReadOnlySpan<byte> preamble = new byte[] {{");
            AddBytes(source, Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Server: B\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Content-Type: "));
            AddBytes(source, Encoding.UTF8.GetBytes(contentType));
            AddBytes(source, Encoding.UTF8.GetBytes("\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Content-Length: "));
            source.Append($@" }};

            writer.Write(preamble);

            var lengthWriter = writer;
            ReadOnlySpan<byte> contentLengthGap = new byte[] {{");
            AddBytes(source, Encoding.UTF8.GetBytes(new string(' ', 4)));
            source.Append($@" }};
            writer.Write(contentLengthGap);

            // Date header
            writer.Write(Utilities.DateHeaderBytes);

            writer.Commit();

            Utf8JsonWriter utf8JsonWriter = Utilities.GetJsonWriter(pipeWriter);
            // Body
            JsonSerializer.Serialize(utf8JsonWriter, {data}, Utilities.SerializerOptions);

            // Content-Length
            lengthWriter.WriteNumeric((uint)utf8JsonWriter.BytesCommitted);
        }}
");
        }

        private static void OutputStringMethod(StringBuilder source, MethodDeclarationSyntax method, string methodName, string contentType, bool isAsync)
        {
            var exp = method.ExpressionBody;
            var literal = exp.Expression as LiteralExpressionSyntax;
            var data = literal.Token.ValueText;

            if (isAsync)
            {
                source.Append($@"
        private static Task {methodName}_Routed(PipeWriter pipeWriter)
        {{
            var writer = GetWriter(pipeWriter, sizeHint: 85 + Utilities.DateHeaderBytes.Length);
            {methodName}_Routed_Task(ref writer);
            writer.Commit();
            return Task.CompletedTask;
        }}");
            }

            source.Append($@"
        private static void {methodName}_Routed{(isAsync ? "_Task" : "")}(ref BufferWriter<WriterAdapter> writer)
        {{
            ReadOnlySpan<byte> preamble = new byte[] {{");

            AddBytes(source, Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Server: B\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Content-Type: "));
            AddBytes(source, Encoding.UTF8.GetBytes(contentType));
            AddBytes(source, Encoding.UTF8.GetBytes("\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Content-Length: "));
            AddBytes(source, Encoding.UTF8.GetBytes(data.Length.ToString(CultureInfo.InvariantCulture)));

            source.Append($@" }};

            writer.Write(preamble);
            // Date header
            writer.Write(Utilities.DateHeaderBytes);

            // Body
            ReadOnlySpan<byte> data = new byte[] {{");

            AddBytes(source, Encoding.UTF8.GetBytes(data));

            source.Append($@" }};
            writer.Write(data);
        }}
");
        }

        private void AddDefaultRoutes(StringBuilder source)
        {
            source.Append($@"
        private static void Default_Routed(ref BufferWriter<WriterAdapter> writer)
        {{
            ReadOnlySpan<byte> preamble = new byte[] {{");
            AddBytes(source, Encoding.UTF8.GetBytes("HTTP/1.1 404 Not Found\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Server: B\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Content-Type: text/plain\r\n"));
            AddBytes(source, Encoding.UTF8.GetBytes("Content-Length: 0"));
            source.Append($@" }};

            writer.Write(preamble);

            // Date header
            writer.Write(Utilities.DateHeaderBytes);
        }}

        private static Task Default_Routed_Task(PipeWriter pipeWriter)
        {{
            var writer = GetWriter(pipeWriter, sizeHint: 85 + Utilities.DateHeaderBytes.Length);
            Default_Routed(ref writer);
            writer.Commit();
            return Task.CompletedTask;
        }}
");

        }

        static void AddBytes(StringBuilder source, byte[] dataBytes)
        {
            foreach (byte b in dataBytes)
            {
                source.Append($"0x{b:X2}, ");
            }
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            // Register a syntax receiver that will be created for each generation pass
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver());
        }


        /// <summary>
        /// Created on demand before each generation pass
        /// </summary>
        class SyntaxReceiver : ISyntaxReceiver
        {
            public List<FieldDeclarationSyntax> CandidateFields { get; } = new List<FieldDeclarationSyntax>();
            public List<MethodDeclarationSyntax> CandidateMethods { get; } = new List<MethodDeclarationSyntax>();

            /// <summary>
            /// Called for every syntax node in the compilation, we can inspect the nodes and save any information useful for generation
            /// </summary>
            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                // any method with at least one attribute is a candidate for property generation
                if (syntaxNode is MethodDeclarationSyntax methodDeclarationSyntax
                    && methodDeclarationSyntax.AttributeLists.Count > 0)
                {
                    CandidateMethods.Add(methodDeclarationSyntax);
                }
            }
        }

        //        public void Execute(GeneratorExecutionContext context)
        //        {
        //            string attributeSource = @"
        //    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple=false)]
        //    internal sealed class MustacheViewAttribute: System.Attribute
        //    {
        //        public string Name { get; }
        //        public string Template { get; }
        //        public string Hash { get; }
        //        public MustacheAttribute(string name, string template, string hash)
        //            => (Name, Template, Hash) = (name, template, hash);
        //    }
        //";
        //            context.AddSource("Mustache_MainAttributes__", SourceText.From(attributeSource, Encoding.UTF8));

        //            Compilation compilation = context.Compilation;

        //            IEnumerable<(string, string, string)> options = GetMustacheOptions(compilation);
        //            IEnumerable<(string, string)> namesSources = SourceFilesFromMustachePaths(options);

        //            foreach ((string name, string source) in namesSources)
        //            {
        //                context.AddSource($"Mustache{name}", SourceText.From(source, Encoding.UTF8));
        //            }
        //        }

        static string SourceFileFromMustachePath(string name, string template, string hash)
        {
            Func<object, string> tree = HandlebarsDotNet.Handlebars.Compile(template);
            object @object = Newtonsoft.Json.JsonConvert.DeserializeObject(hash);
            string mustacheText = tree(@object);

            return GenerateMustacheClass(name, mustacheText);
        }

        static IEnumerable<(string, string)> SourceFilesFromMustachePaths(IEnumerable<(string, string, string)> pathsData)
        {

            foreach ((string name, string template, string hash) in pathsData)
            {
                yield return (name, SourceFileFromMustachePath(name, template, hash));
            }
        }

        private static string GenerateMustacheClass(string className, string mustacheText)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($@"
        namespace Mustache {{

            public static partial class Constants {{

                public const string {className} = @""{mustacheText.Replace("\"", "\"\"")}"";
            }}
        }}
        ");
            return sb.ToString();

        }

        //        public void Initialize(GeneratorInitializationContext context)
        //        {
        //            // No initialization required
        //        }
    }
}