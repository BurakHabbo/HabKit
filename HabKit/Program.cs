﻿using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using HabKit.Commands;
using HabKit.Utilities;

using Flazzy;
using Flazzy.IO;
using Flazzy.ABC;
using Flazzy.Tags;

using Sulakore.Habbo.Web;
using Sulakore.Habbo.Messages;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HabKit
{
    public class Program
    {
        private readonly string _baseDirectory;

        private const string EXTERNAL_VARIABLES_URL = "https://www.habbo.com/gamedata/external_variables";
        private const string CHROME_USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/56.0.2924.87 Safari/537.36";

        public Incoming In { get; }
        public Outgoing Out { get; }
        public HGame Game { get; set; }
        public HBOptions Options { get; }

        public Program(string[] args)
        {
            _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            In = new Incoming();
            Out = new Outgoing();

            Options = HBOptions.Parse(args);
            if (string.IsNullOrWhiteSpace(Options.FetchRevision))
            {
                Game = new HGame(Options.GameInfo.FullName);
                if (Options.Compression == null)
                {
                    Options.Compression = Game.Compression;
                }
            }
            if (string.IsNullOrWhiteSpace(Options.OutputDirectory))
            {
                if (Options.GameInfo == null)
                {
                    Options.OutputDirectory = Environment.CurrentDirectory;
                }
                else
                {
                    Options.OutputDirectory = Options.GameInfo.DirectoryName;
                }
            }
            else
            {
                Options.OutputDirectory = Path.Combine(
                    Environment.CurrentDirectory, Options.OutputDirectory);
            }
        }
        public static void Main(string[] args)
        {
            try
            {
                Console.CursorVisible = false;
                Console.Title = "HabKit v" + FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;
                new Program(args).Run();
            }
            finally { Console.CursorVisible = true; }
        }

        private void Run()
        {
            if (!string.IsNullOrWhiteSpace(Options.FetchRevision))
            {
                ConsoleEx.WriteLineTitle("Fetching");
                Fetch();
            }
            if (Options.Actions.HasFlag(CommandActions.Disassemble))
            {
                ConsoleEx.WriteLineTitle("Disassembling");
                Disassemble();

                if (Options.Actions.HasFlag(CommandActions.Modify))
                {
                    ConsoleEx.WriteLineTitle("Modifying");
                    Modify();
                }

                // Perform this right after modification, in case the '/clean', and '/dump' command combination is present.
                if (Options.Actions.HasFlag(CommandActions.Extract))
                {
                    ConsoleEx.WriteLineTitle("Extracting");
                    Extract();
                }

                if (Options.Actions.HasFlag(CommandActions.Assemble))
                {
                    ConsoleEx.WriteLineTitle("Assembling");
                    Assemble();
                }
            }
        }
        private void Fetch()
        {
            var flashClientUrl = string.Empty;
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = CHROME_USER_AGENT;
                using (var gameDataStream = new StreamReader(client.OpenRead(EXTERNAL_VARIABLES_URL)))
                {
                    while (!gameDataStream.EndOfStream)
                    {
                        string line = gameDataStream.ReadLine();
                        if (!line.StartsWith("flash.client.url")) continue;

                        int urlStart = line.IndexOf('=') + 1;
                        flashClientUrl = "http:" + line.Substring(urlStart) + "Habbo.swf";

                        int revisionStart = line.IndexOf("gordon/") + 7;
                        string revision = line.Substring(revisionStart, line.Length - revisionStart - 1);

                        if (Options.FetchRevision == "?")
                        {
                            Options.FetchRevision = revision;
                        }
                        else
                        {
                            flashClientUrl = flashClientUrl.Replace(
                                revision, Options.FetchRevision);
                        }
                        break;
                    }
                }

                var remoteUri = new Uri(flashClientUrl);
                Options.GameInfo = new FileInfo(Path.Combine(Options.OutputDirectory, remoteUri.LocalPath.Substring(8)));
                Options.OutputDirectory = Directory.CreateDirectory(Options.GameInfo.DirectoryName).FullName;

                Console.Write($"Downloading Client({Options.FetchRevision})...");
                client.DownloadFile(remoteUri, Options.GameInfo.FullName);
                ConsoleEx.WriteLineFinished();
            }

            Game = new HGame(Options.GameInfo.FullName);
            if (Options.Compression == null)
            {
                Options.Compression = Game.Compression;
            }
        }
        private void Modify()
        {
            if (Options.BinRepInfo != null)
            {
                var toReplaceIds = string.Join(", ", Options.BinRepInfo.Replacements.Keys);
                Console.Write($"Replacing Binary Data({toReplaceIds})...");

                foreach (DefineBinaryDataTag defBinData in Game.Tags
                    .Where(t => t.Kind == TagKind.DefineBinaryData))
                {
                    byte[] data = null;
                    if (Options.BinRepInfo.Replacements.TryGetValue(defBinData.Id, out data))
                    {
                        defBinData.Data = data;
                        Options.BinRepInfo.Replacements.Remove(defBinData.Id);
                    }
                }
                if (Options.BinRepInfo.Replacements.Count > 0)
                {
                    var failedReplaceIds = string.Join(", ", Options.BinRepInfo.Replacements.Keys);
                    Console.Write($" | Replacement Failed({failedReplaceIds})");
                }
                ConsoleEx.WriteLineFinished();
            }

            if (Options.ImgRepInfo != null)
            {
                var toReplaceIds = string.Join(", ", Options.ImgRepInfo.Replacements.Keys);
                Console.Write($"Replacing Images({toReplaceIds})...");

                foreach (DefineBitsLossless2Tag defineBitsTag in Game.Tags
                    .Where(t => t.Kind == TagKind.DefineBitsLossless2))
                {
                    Color[,] replacement = null;
                    if (Options.ImgRepInfo.Replacements.TryGetValue(defineBitsTag.Id, out replacement))
                    {
                        defineBitsTag.SetARGBMap(replacement);
                        Options.ImgRepInfo.Replacements.Remove(defineBitsTag.Id);
                    }
                }
                if (Options.ImgRepInfo.Replacements.Count > 0)
                {
                    var failedReplaceIds = string.Join(", ", Options.ImgRepInfo.Replacements.Keys);
                    Console.Write($" | Replacement Failed({failedReplaceIds})");
                }
                ConsoleEx.WriteLineFinished();
            }

            if (Options.CleanInfo != null)
            {
                Console.Write($"Sanitizing({Options.CleanInfo.Sanitizations})...");
                Game.Sanitize(Options.CleanInfo.Sanitizations);
                ConsoleEx.WriteLineFinished();
            }

            if (Options.HardEPInfo != null)
            {
                Console.Write("Injecting Endpoint...");
                Game.InjectEndPoint(Options.HardEPInfo.Address.Host, Options.HardEPInfo.Address.Port).WriteLineResult();
            }

            if (Options.KeyShouterId != null)
            {
                Console.Write($"Injecting RC4 Key Shouter(Message ID: {Options.KeyShouterId})...");
                Game.InjectKeyShouter((int)Options.KeyShouterId).WriteLineResult();
            }

            if (Options.IsDisablingHandshake)
            {
                Console.Write("Disabling Handshake...");
                Game.DisableHandshake().WriteLineResult();
            }

            if (Options.RSAInfo != null)
            {
                Console.Write("Replacing RSA Keys...");
                Game.InjectRSAKeys(Options.RSAInfo.Exponent, Options.RSAInfo.Modulus).WriteLineResult();
            }

            if (Options.IsDisablingHostChecks)
            {
                Console.Write("Disabling Host Checks...");
                Game.DisableHostChecks().WriteLineResult();
            }

            if (Options.IsAddingBackGameCenter)
            {
                Console.Write("Enabling GameCenter...");
                Game.EnableGameCenterIcon().WriteLineResult();
            }

            if (Options.IsInjectingRawCamera)
            {
                Console.Write("Injecting Raw Camera...");
                Game.InjectRawCamera().WriteLineResult();
            }

            if (!string.IsNullOrWhiteSpace(Options.DebugLogger))
            {
                Console.Write($"Injecting Debug Logger(\"{Options.DebugLogger}\")...");
                Game.InjectDebugLogger(Options.DebugLogger).WriteLineResult();
            }

            if (!string.IsNullOrWhiteSpace(Options.MessageLogger))
            {
                Console.Write($"Injecting Message Logger(\"{Options.MessageLogger}\")...");
                Game.InjectMessageLogger(Options.MessageLogger).WriteLineResult();
            }

            if (!string.IsNullOrWhiteSpace(Options.Revision))
            {
                ConsoleEx.WriteLineChanged("Internal Revision Updated", Game.Revision, Options.Revision);
                Game.Revision = Options.Revision;
            }
        }
        private void Extract()
        {
            if (Options.DumpInfo != null || Options.MatchInfo != null)
            {
                Console.Write("Generating Message Hashes...");
                Game.GenerateMessageHashes();
                ConsoleEx.WriteLineFinished();

                string hashesPath = _baseDirectory + "Hashes.ini";
                In.Load(Game, hashesPath);
                Out.Load(Game, hashesPath);
            }

            if (Options.DumpInfo != null)
            {
                string msgsPath = Path.Combine(Options.OutputDirectory, "Messages.txt");
                using (var msgsOutput = new StreamWriter(msgsPath, false))
                {
                    msgsOutput.WriteLine("// " + Game.Revision);
                    msgsOutput.WriteLine();

                    msgsOutput.WriteLine("// Outgoing Messages | " + Game.OutMessages.Count.ToString("n0"));
                    WriteMessages(msgsOutput, "Outgoing", Game.OutMessages);

                    msgsOutput.WriteLine();

                    msgsOutput.WriteLine("// Incoming Messages | " + Game.InMessages.Count.ToString("n0"));
                    WriteMessages(msgsOutput, "Incoming", Game.InMessages);

                    Console.WriteLine("Messages Saved: " + msgsPath);
                }
                string identitiesPath = Path.Combine(Options.OutputDirectory, "Identities.ini");
                using (var output = new StreamWriter(identitiesPath))
                {
                    output.WriteLine(Game.Revision);
                    Out.Save(output);
                    output.WriteLine();
                    In.Save(output);
                }

                if (Options.DumpInfo.IsDumpingImages)
                {
                    var imgDirectory = Directory.CreateDirectory(Options.OutputDirectory + "\\ImageFiles");
                    foreach (DefineBitsLossless2Tag bitsTag in Game.Tags
                        .Where(t => t.Kind == TagKind.DefineBitsLossless2))
                    {
                        string imgPath = Path.Combine(imgDirectory.FullName, "img_" + bitsTag.Id + ".png");
                        Color[,] table = bitsTag.GetARGBMap();

                        int width = table.GetLength(0);
                        int height = table.GetLength(1);
                        using (var asset = new Image<Rgba32>(width, height))
                        {
                            for (int y = 0; y < height; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    Color pixel = table[x, y];
                                    asset[x, y] = new Rgba32(pixel.R, pixel.G, pixel.B, pixel.A);
                                }
                            }
                            using (var output = new StreamWriter(imgPath))
                            {
                                asset.SaveAsPng(output.BaseStream);
                            }
                        }
                    }
                }
                if (Options.DumpInfo.IsDumpingBinaryData)
                {
                    var binDirectory = Directory.CreateDirectory(Options.OutputDirectory + "\\BinaryDataFiles");
                    foreach (DefineBinaryDataTag binTag in Game.Tags
                        .Where(t => t.Kind == TagKind.DefineBinaryData))
                    {
                        string binPath = Path.Combine(binDirectory.FullName, "bin_" + binTag.Id + ".xml");
                        if (Options.DumpInfo.IsMergingBinaryData)
                        {
                            using (var binOutput = File.Open(Options.OutputDirectory + "\\binaryData.xml", FileMode.Append, FileAccess.Write))
                            {
                                binOutput.Write(binTag.Data, 0, binTag.Data.Length);
                            }
                        }
                        else File.WriteAllBytes(binPath, binTag.Data);
                    }
                }
            }

            if (Options.MatchInfo != null)
            {
                MatchCommand matchInfo = Options.MatchInfo;
                using (var previousGame = new HGame(matchInfo.PreviousGameInfo.FullName))
                {
                    Console.Write("Preparing Hash Comparison...");
                    previousGame.Disassemble();
                    previousGame.GenerateMessageHashes();
                    ConsoleEx.WriteLineFinished();

                    Console.Write("Matching Outgoing Messages...");
                    ReplaceHeaders(matchInfo.ClientHeadersInfo, previousGame.OutMessages, previousGame.Revision);
                    ConsoleEx.WriteLineFinished();

                    Console.Write("Matching Incoming Messages...");
                    ReplaceHeaders(matchInfo.ServerHeadersInfo, previousGame.InMessages, previousGame.Revision);
                    ConsoleEx.WriteLineFinished();
                }
            }
        }
        private void Assemble()
        {
            string asmdPath = Path.Combine(Options.OutputDirectory, "asmd_" + Options.GameInfo.Name);
            using (var asmdStream = File.Open(asmdPath, FileMode.Create))
            using (var asmdOutput = new FlashWriter(asmdStream))
            {
                Game.Assemble(asmdOutput, (CompressionKind)Options.Compression);
                Console.WriteLine("File Assembled: " + asmdPath);
            }

            if (Options.RSAInfo != null)
            {
                string keysPath = Path.Combine(Options.OutputDirectory, "RSAKeys.txt");
                using (var keysOutput = new StreamWriter(keysPath, false))
                {
                    keysOutput.WriteLine("[E]Exponent: " + Options.RSAInfo.Exponent);
                    keysOutput.WriteLine("[N]Modulus: " + Options.RSAInfo.Modulus);
                    keysOutput.Write("[D]Private Exponent: " + Options.RSAInfo.PrivateExponent);
                    Console.WriteLine("RSA Keys Saved: " + keysPath);
                }
            }
        }
        private void Disassemble()
        {
            Game.Disassemble();
            if (!Options.OutputDirectory.EndsWith(Game.Revision))
            {
                string directoryName = Path.Combine(Options.OutputDirectory, Game.Revision);
                Options.OutputDirectory = Directory.CreateDirectory(directoryName).FullName;
            }

            var productInfo = (ProductInfoTag)Game.Tags
                .FirstOrDefault(t => t.Kind == TagKind.ProductInfo);

            Console.WriteLine($"Outgoing Messages: {Game.OutMessages.Count:n0}");
            Console.WriteLine($"Incoming Messages: {Game.InMessages.Count:n0}");
            Console.WriteLine("Compilation Date: {0}", productInfo?.CompilationDate.ToString() ?? "?");
            Console.WriteLine("Revision: " + Game.Revision);
        }

        private void WriteMessage(StreamWriter output, MessageItem message)
        {
            ASInstance instance = message.Class.Instance;

            string name = instance.QName.Name;
            string constructorSig = instance.Constructor.ToAS3(true);

            output.Write($"[{message.Id}, {message.Hash}] = {name}{constructorSig}");
            if (!message.IsOutgoing && message.Parser != null)
            {
                output.Write($"[Parser: {message.Parser.Instance.QName.Name}]");
            }
            output.WriteLine();
        }
        private void WriteMessages(StreamWriter output, string title, IDictionary<ushort, MessageItem> messages)
        {
            var deadMessages = new SortedDictionary<ushort, MessageItem>();
            var hashCollisions = new Dictionary<string, SortedList<ushort, MessageItem>>();
            foreach (MessageItem message in messages.Values)
            {
                if (message.References.Count == 0)
                {
                    deadMessages.Add(message.Id, message);
                    continue;
                }
                SortedList<ushort, MessageItem> hashes = null;
                if (!hashCollisions.TryGetValue(message.Hash, out hashes))
                {
                    hashes = new SortedList<ushort, MessageItem>();
                    hashCollisions.Add(message.Hash, hashes);
                }
                hashes.Add(message.Id, message);
            }

            string[] keys = hashCollisions.Keys.ToArray();
            foreach (string hash in keys)
            {
                if (hashCollisions[hash].Count > 1) continue;
                hashCollisions.Remove(hash);
            }

            foreach (MessageItem message in messages.Values)
            {
                if (hashCollisions.ContainsKey(message.Hash)) continue;
                if (message.References.Count == 0) continue;

                output.Write(title);
                WriteMessage(output, message);
            }

            if (hashCollisions.Count > 0)
            {
                output.WriteLine();
                output.WriteLine($"// {title} Message Hash Collisions");
                foreach (SortedList<ushort, MessageItem> hashes in hashCollisions.Values)
                {
                    if (hashes.Count < 2) continue;
                    foreach (MessageItem message in hashes.Values)
                    {
                        output.Write(title);
                        output.Write($"[Collisions: {hashes.Count}]");
                        WriteMessage(output, message);
                    }
                }
            }

            output.WriteLine();
            output.WriteLine($"// {title} Dead Messages");
            foreach (MessageItem message in deadMessages.Values)
            {
                output.Write(title);
                output.Write("[Dead]");
                WriteMessage(output, message);
            }
        }
        private void ReplaceHeaders(FileInfo file, IDictionary<ushort, MessageItem> previousMessages, string revision)
        {
            int totalMatches = 0, matchAttempts = 0;
            using (var input = new StreamReader(file.FullName))
            using (var output = new StreamWriter(Path.Combine(Options.OutputDirectory, file.Name), false))
            {
                if (!Options.MatchInfo.MinimalComments)
                {
                    output.WriteLine("// Current: " + Game.Revision);
                    output.WriteLine("// Previous: " + revision);
                }
                while (!input.EndOfStream)
                {
                    string line = input.ReadLine();
                    int possibleCommentIndex = line.IndexOf("//");
                    if (possibleCommentIndex != -1)
                    {
                        line = Regex.Replace(line, "([a-z]|[A-Z]|\\s)//(.*?)$", string.Empty, RegexOptions.RightToLeft);
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (possibleCommentIndex < line.Length) continue;
                        line = line.TrimEnd();
                    }

                    Match idMatch = null;
                    MatchCollection idMatches = Regex.Matches(line, Options.MatchInfo.Pattern);
                    if (idMatches.Count == 0)
                    {
                        output.WriteLine(line);
                        continue;
                    }

                    if (Options.MatchInfo.IdentifierIndex < 0)
                    {
                        idMatch = idMatches[idMatches.Count - 1]; // Use the LAST matched id.
                    }
                    else
                    {
                        // If the user specified an unexpectedly large index, we'll let them know... by breaking.
                        idMatch = idMatches[Options.MatchInfo.IdentifierIndex]; // Use the matched id at the specified(or default) index.
                    }

                    string prefix = line.Substring(0, idMatch.Index).Replace(revision, Game.Revision);
                    string suffix = line.Substring(idMatch.Index + idMatch.Length).Replace(revision, Game.Revision);
                    output.Write(prefix);

                    matchAttempts++;
                    MessageItem match = null;
                    var comment = string.Empty;
                    if (!ushort.TryParse(idMatch.Value, out ushort id))
                    {
                        matchAttempts--;
                        output.Write("-1");
                        comment = " //! Invalid Message ID: " + idMatch.Value;
                    }
                    else if (!previousMessages.TryGetValue(id, out MessageItem prevMessage))
                    {
                        matchAttempts--;
                        output.Write("-1");
                        comment = " //! Message Not Found: " + id;
                    }
                    else if (!Game.Messages.TryGetValue(prevMessage.Hash, out List<MessageItem> matches))
                    {
                        output.Write("-1");
                        comment = " //! No Matches: " + id;
                    }
                    else match = prevMessage.GetClosestMatch(matches);

                    if (match != null)
                    {
                        totalMatches++;
                        comment = " // " + id;
                        output.Write(Options.MatchInfo.IsOutputtingHashes ? match.Hash : match.Id.ToString());
                    }
                    output.Write(suffix);

                    if (!Options.MatchInfo.MinimalComments)
                    {
                        output.Write(comment);
                        if (!string.IsNullOrWhiteSpace(match?.Structure))
                        {
                            output.Write(" | " + match.Structure);
                        }
                    }
                    output.WriteLine();
                }
            }
            Console.Write($" | Matches: {totalMatches}/{matchAttempts}");
        }
    }
}