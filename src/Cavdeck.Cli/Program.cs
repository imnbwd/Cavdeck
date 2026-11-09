using System.Text;
using Cavdeck.Cli;

Console.OutputEncoding = new UTF8Encoding(false);
return await CliRuntime.RunAsync(args);
