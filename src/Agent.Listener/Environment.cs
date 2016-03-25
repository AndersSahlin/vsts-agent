using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Listener
{
    [ServiceLocator(Default = typeof(Environment))]
    public interface IEnvironment : IAgentService
    {
        Task<Dictionary<string, string>> GetCapabilities(string agentName, CancellationToken token);

        void EnsureEnvFile();

        IDictionary<string, string> GetEnv();
    }

    struct Capability
    {
        public Capability(string name, string tool, string[] paths)
        {
            Name = name;
            Tool = tool;
            Paths = paths;
        }

        //Name of the capability as requried by the task definition.
        public string Name { get; private set; }

        //Name of an executable file, which if found in the file system means the capability is present.
        public string Tool { get; private set; }

        //An array of paths, which if found in the file system means the capability is present.
        public string[] Paths { get; private set; }
    }

    struct ShellCapability
    {
        public ShellCapability(string name, string command, string commandArgs)
        {
            Name = name;
            Command = command;
            CommandArgs = commandArgs;
        }

        //Name of the capability as requried by the task definition.
        public string Name { get; private set; }

        //Name of an executable file, which can determine if a capability is present.
        //The script is expected to print on the standard output the value for the capability.
        public string Command { get; private set; }

        //Arguments passed to Command.
        public string CommandArgs { get; private set; }
    }

    //extensions which simplify adding constant capabilities into a list
    static class ListExtensions
    {
        public static void Add(this IList<Capability> list, string name, string tool = null, string[] paths = null)
        {
            list.Add(new Capability(name, tool, paths));
        }

        public static void Add(this IList<ShellCapability> list, string name, string command, string commandArgs = null)
        {
            list.Add(new ShellCapability(name, command, commandArgs));
        }
    }

    public sealed class Environment : AgentService, IEnvironment
    {
        private List<Capability> _regularCapabilities =
            new List<Capability>
            {
                { "ant" },
                { "bundler", "bundle" },
                { "clang" },
                { "cmake" },
                { "curl" },
                { "git" },
                { "gulp" },
                { "java" },
                { "JDK", "javac" },
                { "make" },
                { "maven", "mvn" },
                { "MSBuild", "xbuild" },
                { "node.js", "node" },
                { "node.js", "nodejs" },
                { "npm" },
                { "python" },
                { "python3" },
                { "sh" },
                { "subversion", "svn" },
                { "ruby" },
                { "rake" },
                { "Xamarin.iOS", "mdtool", new string[] { "/Applications/Xamarin Studio.app/Contents/MacOS/mdtool" } },
                { "Xamarin.Android", "mandroid", new string[] { "/Library/Frameworks/Xamarin.Android.framework/Commands/mandroid" } }
            };

        private static readonly List<ShellCapability> _shellCapabilities =
            new List<ShellCapability>
            {
                { "xcode", "xcode-select", "-p" },
            };

        private static readonly string[] _ignoredEnvVariables = new string[] {
            "TERM_PROGRAM",
            "TERM",
            "TERM_PROGRAM_VERSION",
            "SHLVL",
            "ls_colors",
            "comp_wordbreaks"
        };

        // Ignore env vars specified in the 'VSO_AGENT_IGNORE' env var
        private const string EnvIgnore = "VSO_AGENT_IGNORE";

        private Dictionary<string, string> _capsCache;

        public async Task<Dictionary<string, string>> GetCapabilities(string agentName, CancellationToken token)
        {
            if (_capsCache != null)
            {
                return new Dictionary<string, string>(_capsCache);
            }

            var caps = new Dictionary<string, string>();

            GetFilteredEnvironmentVars(caps);

            GetRegularCapabilities(caps, token);

            await GetShellCapabilities(caps, token);

            caps["Agent.Name"] = agentName ?? string.Empty;
            //TODO: figure out what should be the value of Agent.OS
            //XPLAT is using process.platform, which returns 'darwin', 'freebsd', 'linux', 'sunos' or 'win32'
            //windows agent is printing environment variable "OS", which is something like "Windows_NT" even when running Windows 10
            //.Net core API RuntimeInformation.OSDescription is returning "Microsoft Windows 10.0.10586", 
            //"Linux 3.13.0-43-generic #72-Ubuntu SMP Mon Dec 8 19:35:06 UTC 2014", "Darwin 15.4.0 Darwin Kernel Version 15.4.0: Fri Feb 26 22:08:05 PST 2016;"
            caps["Agent.OS"] = RuntimeInformation.OSDescription ?? string.Empty;
            caps["Agent.ComputerName"] = System.Environment.MachineName ?? string.Empty;

            foreach (var cap in caps)
            {
                Trace.Info($"Capability: {cap.Key} Value: {cap.Value}");
            }

            _capsCache = new Dictionary<string, string>(caps);

            return caps;
        }

        private void GetRegularCapabilities(Dictionary<string, string> capabilities, CancellationToken token)
        {
            try
            {
                //TODO: allow paths to embed environment variables with "$VARNAME" or some other syntax and parse them
                var paths = new string[]
                    {
                        Path.Combine(System.Environment.GetEnvironmentVariable("ANDROID_STUDIO") ?? string.Empty, "/tools/android"),
                        Path.Combine(System.Environment.GetEnvironmentVariable("HOME") ?? string.Empty, "/Library/Developer/Xamarin/android-sdk-macosx/tools/android")
                    };
                _regularCapabilities.Add(new Capability("AndroidSDK", "android", paths));
            }
            catch (Exception ex)
            {
                Trace.Error(ex);
            }

            foreach (var cap in _regularCapabilities)
            {
                string capPath = IOUtil.Which(cap.Tool ?? cap.Name);
                if (!string.IsNullOrEmpty(capPath))
                {
                    capabilities.Add(cap.Name, capPath);
                }
                else if (cap.Paths != null)
                {
                    foreach (var path in cap.Paths)
                    {
                        if (File.Exists(path))
                        {
                            capabilities.Add(cap.Name, path);
                            break;
                        }
                    }
                }

                token.ThrowIfCancellationRequested();
            }
        }

        private async Task GetShellCapabilities(Dictionary<string, string> capabilities, CancellationToken token)
        {
            foreach (var cap in _shellCapabilities)
            {
                var toolPath = IOUtil.Which(cap.Command);
                if (string.IsNullOrEmpty(toolPath))
                {
                    continue;
                }

                using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
                {
                    string toolOutput = string.Empty;
                    var outputHandler = new EventHandler<DataReceivedEventArgs>((sender, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                        {
                            toolOutput += args.Data;
                        }
                    });
                    processInvoker.OutputDataReceived += outputHandler;
                    try
                    {
                        await processInvoker.ExecuteAsync(
                                    workingDirectory: string.Empty,
                                    fileName: toolPath,
                                    arguments: cap.CommandArgs,
                                    environment: null,
                                    cancellationToken: token);
                        //TODO: should we check if toolOutput is a valid file path? (XPLAT does not check either)
                        if (!string.IsNullOrEmpty(toolOutput))
                        {
                            capabilities.Add(cap.Name, toolOutput);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Trace.Error(ex);
                    }
                    finally
                    {
                        processInvoker.OutputDataReceived -= outputHandler;
                    }
                }

                token.ThrowIfCancellationRequested();
            }
        }

        private void GetFilteredEnvironmentVars(Dictionary<string, string> vars)
        {
            IDictionary envVars = System.Environment.GetEnvironmentVariables();

            // Begin with ignoring env vars declared herein
            var ignoredEnvVariables = new HashSet<string>(_ignoredEnvVariables);

            // Also ignore env vars specified in the 'VSO_AGENT_IGNORE' env var
            if (envVars.Contains(EnvIgnore))
            {
                var additionalIgnoredVars = ((string)envVars[EnvIgnore]).Split(',');
                foreach (var ignored in additionalIgnoredVars)
                {
                    ignoredEnvVariables.Add(ignored.Trim());
                }
            }

            // Get filtered env vars
            foreach (DictionaryEntry envVar in envVars)
            {
                string varName = (string)envVar.Key;
                string varValue = (string)envVar.Value ?? string.Empty;
                if (!ignoredEnvVariables.Contains(varName) && varValue.Length < 1024)
                {
                    vars.Add(varName, varValue);
                }
            }
        }

        // Ensures existence of the environment file at the specified path, creating it if missing.
        public void EnsureEnvFile()
        {
            string envFileName = IOUtil.GetEnvFilePath();
            if (File.Exists(envFileName))
            {
                return;
            }

            var envVars = new Dictionary<string, string>();
            GetFilteredEnvironmentVars(envVars);

            var content = new StringBuilder(string.Empty);
            foreach (var envvar in envVars)
            {
                content.Append($"{envvar.Key}={envvar.Value}");
                content.AppendLine();
            }

            File.WriteAllText(envFileName, content.ToString(), System.Text.Encoding.UTF8);
        }

        // Gets the environment that the agent and worker will use when running as a service.
        // The current process' environment is overlayed with contents of the environment file.
        // When not running as a service, the interactive/shell process' environment is used.
        public IDictionary<string, string> GetEnv()
        {
            string envFileName = IOUtil.GetEnvFilePath();
            if (!File.Exists(envFileName))
            {
                throw new FileNotFoundException();
            }

            string data = File.ReadAllText(envFileName, System.Text.Encoding.UTF8);

            var env = new Dictionary<string, string>();

            var lines = data.Split(new string[] { System.Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                var tokens = line.Split('=');
                if (tokens.Length == 2)
                {
                    var envKey = (tokens[0] ?? string.Empty).Trim();
                    var envVal = (tokens[1] ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(envKey) && !string.IsNullOrEmpty(envVal))
                    {
                        env[envKey] = envVal;
                    }
                }
            }

            return env;
        }
    }
}
