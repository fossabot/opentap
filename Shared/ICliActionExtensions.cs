//            Copyright Keysight Technologies 2012-2019
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at http://mozilla.org/MPL/2.0/.
using OpenTap.Package;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace OpenTap.Cli
{
    /// <summary>
    /// Helper class that enhances the <see cref="ICliAction">ICliAction</see> with extra extensions methods.
    /// </summary>
    internal static class ICliActionExtensions
    {
        /// <summary>
        /// Executes the action with the given parameters.
        /// </summary>
        /// <param name="action">The action to be executed.</param>
        /// <param name="parameters">The parameters for the action.</param>
        /// <returns></returns>
        public static int PerformExecute(this ICliAction action, string[] parameters)
        {
            ArgumentsParser ap = new ArgumentsParser();
            var td = TypeData.GetTypeData(action);
            if (td == null)
                throw new Exception("Type data is null for action: " + action.GetType().Name);
            var props = td.GetMembers();

            ap.AllOptions.Add("help", 'h', false, "Write help information.");
            ap.AllOptions.Add("verbose", 'v', false, "Show verbose/debug-level log messages.");
            ap.AllOptions.Add("color", 'c', false, "Color messages according to their severity.");
            ap.AllOptions.Add("quiet", 'q', false, "Quiet console logging.");
            ap.AllOptions.Add("log", description: "Specify log file location. Default is ./SessionLogs.");

            var argToProp = new Dictionary<string, IMemberData>();
            var unnamedArgToProp = new List<IMemberData>();

            var overrides = new HashSet<string>();

            foreach (var prop in props)
            {
                if (prop.Readable == false || prop.Writable == false)
                    continue;

                if (prop.HasAttribute<UnnamedCommandLineArgument>())
                {
                    unnamedArgToProp.Add(prop);
                    continue;
                }

                if (!prop.HasAttribute<CommandLineArgumentAttribute>())
                    continue;

                var attr = prop.GetAttribute<CommandLineArgumentAttribute>();

                var needsArg = prop.TypeDescriptor != TypeData.FromType(typeof(bool));

                string description = "";
                if (prop.HasAttribute<ObsoleteAttribute>())
                {
                    var obsoleteAttribute = prop.GetAttribute<ObsoleteAttribute>();
                    description = $"[OBSOLETE: {obsoleteAttribute.Message}] {attr.Description}";
                }
                else
                {
                    description = attr.Description;
                }

                Argument fromAttr(CommandLineArgumentAttribute a)
                {
                    return new Argument(a.Name, a.ShortName.FirstOrDefault(), needsArg);
                }

                if (ap.AllOptions.Contains(attr.Name))
                {
                    ap.AllOptions[attr.Name] = fromAttr(attr);
                    if (overrides.Add(attr.Name))
                        log.Debug(
                            $"The CLI option '--{attr.Name}' from '{action}' overrides a common CLI option from OpenTAP.");
                }

                if (!string.IsNullOrWhiteSpace(attr.ShortName))
                {
                    var overriden = ap.AllOptions.FirstOrDefault(opt => opt.Value?.ShortName == attr.ShortName[0]);
                    if (overriden.Value != null)
                    {
                        ap.AllOptions[overriden.Value.LongName] = fromAttr(attr);
                        if (overrides.Add(overriden.Value.LongName))
                            log.Debug(
                                $"The CLI option '-{attr.ShortName}' from '{action}' overrides the common CLI option '--{overriden.Value.LongName}' from OpenTAP.");
                    }
                }

                var arg = ap.AllOptions.Add(attr.Name, attr.ShortName?.FirstOrDefault() ?? '\0', needsArg, description);
                arg.IsVisible = attr.Visible;
                argToProp.Add(arg.LongName, prop);
            }

            var args = ap.Parse(parameters);

            if (args.MissingArguments.Any())
                throw new Exception($"Command line argument '{args.MissingArguments.FirstOrDefault().LongName}' is missing an argument.");

            if (!overrides.Contains("help") && args.Contains("help"))
            {
                printOptions(action.GetType().GetAttribute<DisplayAttribute>().Name, ap.AllOptions, unnamedArgToProp);
                return (int)ExitCodes.Success;
            }

            if (!overrides.Contains("log") && args.Contains("log"))
            {
                SessionLogs.Rename(args.Argument("log"));
            }

            foreach (var opts in args)
            {
                if (argToProp.ContainsKey(opts.Key) == false) continue; 
                var prop = argToProp[opts.Key];

                if (prop.TypeDescriptor is TypeData propTd)
                {
                    Type propType = propTd.Load();
                    if (propType == typeof(bool)) prop.SetValue(action, true);
                    else if (propType.IsEnum) prop.SetValue(action, ParseEnum(opts.Key, opts.Value.Value, propType));
                    else if (propType == typeof(string)) prop.SetValue(action, opts.Value.Value);
                    else if (propType == typeof(string[])) prop.SetValue(action, opts.Value.Values.ToArray());
                    else if (propType == typeof(int)) prop.SetValue(action, int.Parse(opts.Value.Value));
                    else throw new Exception($"Command line option '{opts.Key}' is of an unsupported type '{propType.Name}'.");
                }
                else
                    throw new Exception($"Command line option '{opts.Key}' is of an unsupported type '{prop.TypeDescriptor.Name}'.");
            }

            unnamedArgToProp = unnamedArgToProp.OrderBy(p => p.GetAttribute<UnnamedCommandLineArgument>().Order).ToList();
            var requiredArgs = unnamedArgToProp.Where(x => x.GetAttribute<UnnamedCommandLineArgument>().Required).ToHashSet();
            int idx = 0;

            for (int i = 0; i < unnamedArgToProp.Count; i++)
            {
                var p = unnamedArgToProp[i];

                if (p.TypeDescriptor.IsA(typeof(string)))
                {
                    if (idx < args.UnnamedArguments.Length)
                    {
                        p.SetValue(action, args.UnnamedArguments[idx++]);
                        requiredArgs.Remove(p);
                    }
                }
                else if (p.TypeDescriptor.IsA(typeof(string[])))
                {
                    if (idx < args.UnnamedArguments.Length)
                    {
                        p.SetValue(action, args.UnnamedArguments.Skip(idx).ToArray());
                        requiredArgs.Remove(p);
                    }

                    idx = args.UnnamedArguments.Length;
                }
                else if (p.TypeDescriptor is TypeData td2 && td2.Type.IsEnum)
                {
                    if (idx < args.UnnamedArguments.Length)
                    {
                        var name = p.GetAttribute<UnnamedCommandLineArgument>()?.Name ?? p.Name;
                        p.SetValue(action, ParseEnum($"<{name}>", args.UnnamedArguments[idx++], td2.Type));
                        requiredArgs.Remove(p);
                    }
                }
            }

            if (args.UnknownsOptions.Any() || requiredArgs.Any())
            {
                if (args.UnknownsOptions.Any())
                    Console.WriteLine("Unknown options: " + string.Join(" ", args.UnknownsOptions));

                if (requiredArgs.Any())
                    Console.WriteLine("Missing argument: " + string.Join(" ", requiredArgs.Select(p => p.GetAttribute<UnnamedCommandLineArgument>().Name)));
                
                printOptions(action.GetType().GetAttribute<DisplayAttribute>().Name, ap.AllOptions, unnamedArgToProp);
                return (int)ExitCodes.ArgumentParseError;
            }

            var actionFullName = td.GetDisplayAttribute().GetFullName();
            log.Debug($"Executing CLI action: {actionFullName}");
            var sw = Stopwatch.StartNew();
            int exitCode = action.Execute(TapThread.Current.AbortToken);
            log.Debug(sw, "CLI action returned exit code: {0}", exitCode);
            return exitCode;
        }

        static TraceSource log = Log.CreateSource("CLI"); 
        
        private static void printOptions(string passName, ArgumentCollection options, List<IMemberData> unnamed)
        {
            Console.WriteLine("Usage: {2} {0} {1}",
                string.Join(" ", options.Values.Where(x => x.IsVisible).Select(x =>
                {
                    var str = x.ShortName != '\0' ? string.Format("-{0}", x.ShortName) : "--" + x.LongName;

                    if (x.NeedsArgument)
                        str += " <arg>";

                    return '[' + str + ']';
                })),
                string.Join(" ", unnamed.Select(x =>
                {
                    var attr = x.GetAttribute<UnnamedCommandLineArgument>();
                    var str = attr.Name;

                    if (attr.Required == false)
                        str = "[<" + str + ">]";
                    else
                        str = "<" + str + ">";

                    return str;
                })), passName);

            Console.Write(options);
        }
        
        private static object ParseEnum(string name, string value, Type propertyType)
        {
            var obj = StringConvertProvider.FromString(value, TypeData.FromType(propertyType), null, System.Globalization.CultureInfo.InvariantCulture);

            if (obj == null)
                throw new Exception(string.Format("Could not parse argument '{0}'. Argument given: '{1}'. Valid arguments: {2}", name, value, string.Join(", ", propertyType.GetEnumNames())));

            return obj;
        }
    }
}
