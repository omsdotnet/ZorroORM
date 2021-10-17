using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ZorroORM
{
  public static class DbConnectionExtension
  {
    public static T rpc<T>(this DbConnection db) where T : class
    {
      var comeType = typeof(T);

      if (!comeType.IsInterface)
      {
        throw new InvalidCastException("Only interfaces allowed");
      }

      return (T)CreateInstance(comeType, db);
    }

    private static string GetClassBody(Type comeType)
    {
      MethodInfo[] info = comeType.GetMethods();

      string exePath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

      var sb = new StringBuilder();

      for (int i = 0; i < info.Length; i++)
      {
        string methodFilePath = $"{exePath}\\{info[i].MethodFileName()}.sql";
        string methodText = File.ReadAllText(methodFilePath).Replace("\"", "\"\"");

        sb.AppendLine($"public {info[i].MethodFullSignature()}");
        sb.AppendLine(@"          {");
        sb.AppendLine(@"            using (var command = dbconnection.CreateCommand())");
        sb.AppendLine(@"            {");
        sb.AppendLine(@"              command.CommandType = CommandType.Text;");
        sb.AppendLine($"              command.CommandText = @\"{methodText}\";");

        if (info[i].ReturnType == typeof(void))
        {
          sb.AppendLine(@"              var result = command.ExecuteNonQuery();");
        }
        else
        {
          sb.AppendLine(@"              var result = command.ExecuteScalar();");
          sb.AppendLine($"              return {GetConverter(info[i].ReturnType, "result")};");
        }

        sb.AppendLine(@"            }");
        sb.Append(@"          }");

        if (i < info.Length - 1)
        {
          sb.AppendLine();
          sb.AppendLine();
          sb.Append(@"          ");
        }
      }

      return sb.ToString();
    }

    private static string GetConverter(Type returnType, string returnValue)
    {
      if (returnType == typeof(object))
      {
        return returnValue;
      }

      if (returnType.IsPrimitive || returnType == typeof(string) || returnType == typeof(DateTime))
      {
        return $"Convert.To{returnType.Name}({returnValue})";
      }

      throw new NotSupportedException($"Return type: {returnType.FullName} - not supported");
    }

    private static object CreateInstance(Type comeType, DbConnection db)
    {
      var usedTypeList = new List<Type>()
      {
        typeof(object),
        typeof(DbConnection),
        typeof(CommandType),
        typeof(Component),
        typeof(Console),
        comeType
      };

      string className = comeType.Name.Substring(1);
      string classNameSpace = typeof(DbConnectionExtension).Namespace;

      string usingSet = GetUsingSet(usedTypeList, classNameSpace);
      string classBody = GetClassBody(comeType);

      string codeToCompile = ProxyClassTemplate
        .Replace(UsingsPlaceholder, usingSet)
        .Replace(NameSpacePlaceholder, classNameSpace)
        .Replace(ClassNamePlaceholder, className)
        .Replace(InterfacePlaceholder, comeType.FullName)
        .Replace(ClassBodyPlaceholder, classBody);

//      Console.WriteLine(codeToCompile);

      SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(codeToCompile);
      string assemblyName = Path.GetRandomFileName();

      var referenceSet = GetReferenceSet(usedTypeList);

      CSharpCompilation compilation = CSharpCompilation.Create(
        assemblyName,
        syntaxTrees: new[] { syntaxTree },
        references: referenceSet,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

      using (var ms = new MemoryStream())
      {
        EmitResult result = compilation.Emit(ms);

        if (!result.Success)
        {
          IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
            diagnostic.IsWarningAsError ||
            diagnostic.Severity == DiagnosticSeverity.Error);
/*
          foreach (Diagnostic diagnostic in failures)
          {
            Console.Error.WriteLine("\t{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
          }
*/
          return null;
        }

        ms.Seek(0, SeekOrigin.Begin);
        byte[] data = new byte[ms.Length];
        ms.Read(data, 0, data.Length);

        var assembly = Assembly.Load(data);

        return assembly.CreateInstance($"{classNameSpace}.{className}", true, BindingFlags.Default, null,
          new object[] { db }, null, null);
      }
    }

    private static List<MetadataReference> GetReferenceSet(List<Type> usedTypeList)
    {
      var ret = new List<MetadataReference>();
      var stringList = new List<string>();

      Type objectType = typeof(object);
      string objectTypeLocation = objectType.GetTypeInfo().Assembly.Location;
      string frameworkBasePath = Path.GetDirectoryName(objectTypeLocation);
      string runtimeLibraryPath = $"{frameworkBasePath}\\System.Runtime.dll";

      stringList.Add(runtimeLibraryPath);

      foreach (var type in usedTypeList)
      {
        var item = type.GetTypeInfo().Assembly.Location;

        if (!stringList.Contains(item))
        {
          stringList.Add(item);
        }
      }

      foreach (var listItem in stringList)
      {
        var item = MetadataReference.CreateFromFile(listItem);

        ret.Add(item);
      }

      return ret;
    }

    private static string GetUsingSet(List<Type> usedTypeList, string excludedNamespace)
    {
      var ret = new List<string>();

      foreach (var type in usedTypeList)
      {
        var item = $"using {type.Namespace};{Environment.NewLine}      ";

        if (!ret.Contains(item) && type.Namespace != excludedNamespace)
        {
          ret.Add(item);
        }
      }

      return string.Join(string.Empty, ret);
    }

    private const string UsingsPlaceholder = "//@USINGS@//";
    private const string NameSpacePlaceholder = "//@NAMESPACE@//";
    private const string ClassNamePlaceholder = "//@CLASS_NAME@//";
    private const string InterfacePlaceholder = "//@IMPLEMENTED_INTERFACE@//";
    private const string ClassBodyPlaceholder = "//@CLASS_BODY@//";

    private static readonly string ProxyClassTemplate =
      $"      {UsingsPlaceholder}" + Environment.NewLine +
      $"      namespace {NameSpacePlaceholder}" + Environment.NewLine +
      @"      {" + Environment.NewLine +
      $"        public class {ClassNamePlaceholder} : {InterfacePlaceholder}" + Environment.NewLine +
      @"        {" + Environment.NewLine +
      @"          private DbConnection dbconnection;" + Environment.NewLine +
      @"" + Environment.NewLine +
      $"          public {ClassNamePlaceholder}(DbConnection db)" + Environment.NewLine +
      @"          {" + Environment.NewLine +
      @"            dbconnection = db;" + Environment.NewLine +
      @"          }" + Environment.NewLine +
      @"" + Environment.NewLine +
      $"          {ClassBodyPlaceholder}" + Environment.NewLine +
      @"        }" + Environment.NewLine +
      @"      }";
  }

  public static class MethodInfoExtension
  {
    public static string MethodFullSignature(this MethodInfo mi)
    {
      string returnTypeName = mi.ReturnType.Name;
      if (returnTypeName == "Void") returnTypeName = "void";

      string[] param = mi.GetParameters()
        .Select(p => $"{p.ParameterType.Name} {p.Name}")
        .ToArray();

      string signature = $"{returnTypeName} {mi.Name}({string.Join(", ", param)})";

      return signature;
    }

    public static string MethodFileName(this MethodInfo mi)
    {
      string[] param = mi.GetParameters()
        .Select(p => $"{p.ParameterType.Name} {p.Name}")
        .ToArray();

      string signature = $"{mi.DeclaringType.Name}.{mi.Name}({string.Join(", ", param)})";

      return signature;
    }

    private static readonly Regex ParamPrefixes = new Regex("[@:?].+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string InlineFormatter(string commandText, DbParameterCollection parameters)
    {

      if (parameters == null || parameters.Count == 0)
      {
        return commandText;
      }

      foreach (DbParameter p in parameters)
      {
        var name = ParamPrefixes.IsMatch(p.ParameterName)
          ? p.ParameterName
          : Regex.Match(commandText, "([@:?])" + p.ParameterName, RegexOptions.IgnoreCase).Value;

        if (!string.IsNullOrWhiteSpace(name))
        {
          var value = GetParameterValue(p);
          commandText = Regex.Replace(commandText, "(" + name + ")([^0-9A-z]|$)", m => value + m.Groups[2], RegexOptions.IgnoreCase);
        }
      }

      return commandText;
    }

    private static string GetParameterValue(DbParameter param)
    {
      var result = param.Value.ToString();
      var type = param.DbType;

      switch (type)
      {
        case DbType.AnsiString:
        case DbType.AnsiStringFixedLength:
        case DbType.StringFixedLength:
        case DbType.String:
        case DbType.Date:
        case DbType.DateTime:
        case DbType.DateTime2:
        case DbType.DateTimeOffset:
          result = string.Format("'{0}'", result);
          break;
      }

      return result;
    }
  }
}
