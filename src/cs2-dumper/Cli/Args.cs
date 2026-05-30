using System.Globalization;

namespace Cs2Dumper.Cli;

public sealed class ArgException(string message) : Exception(message);

/// <summary>
/// Command-line options, mirroring the reference clap definition. <c>--connector</c> /
/// <c>--connector-args</c> are accepted for compatibility but ignored: this port always reads
/// the target process directly through the OS (the equivalent of the default memflow-native path).
/// </summary>
public sealed class Args
{
    private static readonly string[] DefaultFileTypes = { "cs", "hpp", "json", "rs", "zig" };

    public string? Connector { get; private set; }
    public string? ConnectorArgs { get; private set; }
    public List<string> FileTypes { get; private set; } = new(DefaultFileTypes);
    public int IndentSize { get; private set; } = 4;
    public string Output { get; private set; } = "output";
    public string ProcessName { get; private set; } = "cs2.exe";
    public int Verbose { get; private set; }
    public bool NoLogFile { get; private set; }

    /// <summary>Returns null when help/version was printed (caller should exit successfully).</summary>
    public static Args? Parse(string[] argv)
    {
        var args = new Args();
        bool fileTypesProvided = false;

        for (int i = 0; i < argv.Length; i++)
        {
            string token = argv[i];

            if (token == "--")
            {
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                string name = token[2..];
                string? inlineValue = null;
                int eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    inlineValue = name[(eq + 1)..];
                    name = name[..eq];
                }

                string Value() => inlineValue ?? Next(argv, ref i, name);

                switch (name)
                {
                    case "connector": args.Connector = Value(); break;
                    case "connector-args": args.ConnectorArgs = Value(); break;
                    case "file-types": AddFileTypes(args, Value(), ref fileTypesProvided); break;
                    case "indent-size": args.IndentSize = ParseInt(Value(), "indent-size"); break;
                    case "output": args.Output = Value(); break;
                    case "process-name": args.ProcessName = Value(); break;
                    case "verbose": args.Verbose++; break;
                    case "no-log-file": args.NoLogFile = true; break;
                    case "help": PrintHelp(); return null;
                    case "version": PrintVersion(); return null;
                    default: throw new ArgException($"unexpected argument '--{name}'");
                }
            }
            else if (token.Length > 1 && token[0] == '-')
            {
                int j = 1;
                while (j < token.Length)
                {
                    char c = token[j];
                    switch (c)
                    {
                        case 'v': args.Verbose++; j++; break;
                        case 'n': args.NoLogFile = true; j++; break;
                        case 'h': PrintHelp(); return null;
                        case 'V': PrintVersion(); return null;
                        default:
                            string value = ShortValue(token, ref j, argv, ref i, c);
                            switch (c)
                            {
                                case 'c': args.Connector = value; break;
                                case 'a': args.ConnectorArgs = value; break;
                                case 'f': AddFileTypes(args, value, ref fileTypesProvided); break;
                                case 'i': args.IndentSize = ParseInt(value, "indent-size"); break;
                                case 'o': args.Output = value; break;
                                case 'p': args.ProcessName = value; break;
                                default: throw new ArgException($"unexpected argument '-{c}'");
                            }

                            break;
                    }
                }
            }
            else
            {
                throw new ArgException($"unexpected argument '{token}'");
            }
        }

        return args;
    }

    private static string Next(string[] argv, ref int i, string name)
    {
        if (i + 1 >= argv.Length)
        {
            throw new ArgException($"a value is required for '--{name}'");
        }

        return argv[++i];
    }

    private static string ShortValue(string token, ref int j, string[] argv, ref int i, char c)
    {
        // -oVALUE, -o=VALUE, or -o VALUE
        if (j + 1 < token.Length)
        {
            string rest = token[(j + 1)] == '=' ? token[(j + 2)..] : token[(j + 1)..];
            j = token.Length;
            return rest;
        }

        j = token.Length;
        if (i + 1 >= argv.Length)
        {
            throw new ArgException($"a value is required for '-{c}'");
        }

        return argv[++i];
    }

    private static void AddFileTypes(Args args, string value, ref bool provided)
    {
        if (!provided)
        {
            args.FileTypes = new List<string>();
            provided = true;
        }

        foreach (string part in value.Split(','))
        {
            if (part.Length > 0)
            {
                args.FileTypes.Add(part);
            }
        }
    }

    private static int ParseInt(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result < 0)
        {
            throw new ArgException($"invalid value '{value}' for '--{name}'");
        }

        return result;
    }

    private static void PrintVersion() => Console.WriteLine("cs2-dumper 0.1.3");

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            An external offset/interface dumper for Counter-Strike 2 (.NET AOT port).

            Usage: cs2-dumper [OPTIONS]

            Options:
              -c, --connector <connector>            (accepted for compatibility, ignored; native reader is always used)
              -a, --connector-args <connector-args>  (accepted for compatibility, ignored)
              -f, --file-types <file-types>          Types of files to generate [default: cs,hpp,json,rs,zig]
              -i, --indent-size <indent-size>        Spaces per indentation level [default: 4]
              -o, --output <output>                  Output directory [default: output]
              -p, --process-name <process-name>      Game process name [default: cs2.exe]
              -v...                                  Increase logging verbosity (repeatable)
              -n, --no-log-file                      Prevent creation of the cs2-dumper.log file
              -h, --help                             Print help
              -V, --version                          Print version
            """);
    }
}
