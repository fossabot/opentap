//            Copyright Keysight Technologies 2012-2019
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at http://mozilla.org/MPL/2.0/.
using OpenTap;
using OpenTap.Cli;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OpenTap.Sdk.New
{
    public abstract class GenerateType : ICliAction
    {
        internal bool Validate(string name, bool allowWhiteSpace, bool allowLeadingNumbers)
        {
            bool anyInvalid = false;
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();

            var leading = !allowLeadingNumbers;

            foreach (var ch in name)
            {
                if (leading && char.IsNumber(ch))
                {
                    sb.Append("^");
                    anyInvalid = true;
                    continue;
                }

                leading = false;

                // Then detect any invalid filename or C# identifier chars
                if (invalid.Contains(ch) || (!allowWhiteSpace && char.IsWhiteSpace(ch)))
                {
                    sb.Append("^");
                    anyInvalid = true;
                }
                else sb.Append(" ");
            }

            if (!anyInvalid) return true;

            var stringStart = "Invalid name specified: '";
            log.Error($"{stringStart}{name}' contains illegal characters.");
            var hint = sb.ToString();
            log.Error(hint.PadLeft(stringStart.Length + hint.Length));
            return false;
        }

        public TraceSource log = Log.CreateSource("New");

        [CommandLineArgument("out", ShortName = "o", Description = "Path to generated file.")]
        public virtual string output { get; set; }

        private string workingDirectory;

        internal string WorkingDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(workingDirectory))
                    return Directory.GetCurrentDirectory();
                return workingDirectory;
            }
            set => workingDirectory = value;
        }

        public abstract int Execute(CancellationToken cancellationToken);

        public bool WriteFile(string filepath, string content, bool force = false)
        {
            if (File.Exists(filepath) && force == false)
            {
                log.Error("File already exists: '{0}'", Path.GetFileName(filepath));
                log.Info("Do you want to override?");

                var request = new OverrideRequest();
                UserInput.Request(request, true);

                if (request.Override == RequestEnum.No)
                {
                    log.Info("File was not overridden.");
                    return false;
                }
            }

            if (!Directory.Exists(Path.GetDirectoryName(filepath)) && string.IsNullOrWhiteSpace(Path.GetDirectoryName(filepath)) == false)
                Directory.CreateDirectory(Path.GetDirectoryName(filepath));

            File.WriteAllText(filepath, content);
            log.Info($"Generated file: '{filepath}'.");

            return true;
        }

        protected string TryGetNamespace()
        {
            string dir = output;
            if (output == null)
                dir = WorkingDirectory;
            else if (output.EndsWith("/") == false)
                dir = Path.GetDirectoryName(dir);

            var csprojFiles = Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories);
            var csprojPath = csprojFiles.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(csprojPath) == false)
            {
                var match = Regex.Match(File.ReadAllText(csprojPath), "<RootNamespace>(.*/)</RootNamespace>");
                if (match.Success)
                    return match.Groups[1].Value;
                else
                    return Path.GetFileNameWithoutExtension(csprojPath);
            }

            throw new Exception($"Could not find project file ('.csproj') in '{dir}'.\nNote: You can create a new project with 'tap sdk new project <name>'.");
        }

        protected string ReplaceInTemplate(string content, params string[] fields)
        {
            content = Regex.Replace(content, "\\{(\\d)\\}", (m) =>
            {
                if (int.TryParse(m.Groups[1].Value, out int index) && index < fields.Length)
                    return fields[index];
                else
                    return "";
            });
            return content;
        }
    }

    public class OverrideRequest
    {
        [Layout(LayoutMode.FloatBottom | LayoutMode.FullRow)]
        [Submit]
        public RequestEnum Override { get; set; }
    }

    public enum RequestEnum
    {
        No,
        Yes
    }
}
