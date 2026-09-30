using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveB1;
using GurGsPersistent;
using Google.Protobuf;

namespace WukongSaveReader
{
    class Program
    {
        private static readonly byte[] MagicKey = BitConverter.GetBytes(4024806562674138235L);
        private static readonly string[] GameProcessNames = { "b1-Win64-Shipping", "b1" };

        private static int _currentDeaths = -1;

        private static readonly List<StreamWriter> SseClients = new();
        private static readonly object ClientsLock = new();

        static void Main(string[] args)
        {
            try
            {
                var (savePath, name, level, deaths) = SelectSaveFile();
                _currentDeaths = deaths;

                PrintCard(name, level, deaths);

                StartOverlayServer();

                DateTime lastWriteTime = File.GetLastWriteTimeUtc(savePath);

                while (true)
                {
                    Thread.Sleep(1000);

                    try
                    {
                        DateTime currentWriteTime = File.GetLastWriteTimeUtc(savePath);
                        if (currentWriteTime != lastWriteTime)
                        {
                            lastWriteTime = currentWriteTime;
                            Thread.Sleep(200);

                            var (_, newLevel, newDeaths) = ReadSaveData(savePath);

                            if (newDeaths != _currentDeaths)
                            {
                                _currentDeaths = newDeaths;
                                PrintCard(name, newLevel, newDeaths);
                                BroadcastDeaths(newDeaths);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.ReadKey(true);
            }
        }

        private static (string Path, string Name, int Level, int Deaths) SelectSaveFile()
        {
            Console.WriteLine("Waiting for Black Myth: Wukong to start...");

            Process? gameProcess = null;

            while (gameProcess == null)
            {
                foreach (var name in GameProcessNames)
                {
                    var processes = Process.GetProcessesByName(name);
                    if (processes.Length > 0)
                    {
                        gameProcess = processes[0];
                        break;
                    }
                }

                if (gameProcess == null)
                {
                    Thread.Sleep(1000);
                }
            }

            string? exePath = gameProcess.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                throw new FileNotFoundException("Unable to determine the game executable path.");
            }

            string binariesDir = Path.GetDirectoryName(exePath)!;
            string saveGamesDir = Path.GetFullPath(Path.Combine(binariesDir, @"..\..\Saved\SaveGames"));

            if (!Directory.Exists(saveGamesDir))
            {
                throw new DirectoryNotFoundException($"Save directory not found: {saveGamesDir}");
            }

            var profileDirs = Directory.GetDirectories(saveGamesDir);
            if (profileDirs.Length == 0)
            {
                throw new DirectoryNotFoundException("No player profile folders found.");
            }

            string selectedProfileDir = profileDirs[0];

            if (profileDirs.Length > 1)
            {
                Console.WriteLine("\nMultiple Steam profiles found. Select one:");
                for (int i = 0; i < profileDirs.Length; i++)
                {
                    string steamId = Path.GetFileName(profileDirs[i]);
                    Console.WriteLine($" [{i + 1}] {steamId}");
                }

                int choiceIndex = -1;
                while (choiceIndex < 0 || choiceIndex >= profileDirs.Length)
                {
                    Console.Write($"Enter choice (1-{profileDirs.Length}): ");
                    if (int.TryParse(Console.ReadLine(), out int val) && val >= 1 && val <= profileDirs.Length)
                    {
                        choiceIndex = val - 1;
                    }
                }
                selectedProfileDir = profileDirs[choiceIndex];
            }

            var availableSlots = new List<(int Slot, string FilePath, string Name, int Level, int Deaths)>();

            for (int i = 1; i <= 10; i++)
            {
                string filePath = Path.Combine(selectedProfileDir, $"ArchiveSaveFile.{i}.sav");
                if (File.Exists(filePath))
                {
                    try
                    {
                        var data = ReadSaveData(filePath);
                        availableSlots.Add((i, filePath, data.Name, data.Level, data.Deaths));
                    }
                    catch
                    {
                    }
                }
            }

            if (availableSlots.Count == 0)
            {
                throw new FileNotFoundException("No character save files (ArchiveSaveFile.1..10.sav) found.");
            }

            if (availableSlots.Count == 1)
            {
                var single = availableSlots[0];
                return (single.FilePath, single.Name, single.Level, single.Deaths);
            }

            Console.WriteLine("\nMultiple characters found. Select a character:");
            for (int i = 0; i < availableSlots.Count; i++)
            {
                var slot = availableSlots[i];
                Console.WriteLine($" [{i + 1}] Slot {slot.Slot}: {slot.Name} (Level {slot.Level})");
            }

            int slotChoice = -1;
            while (slotChoice < 0 || slotChoice >= availableSlots.Count)
            {
                Console.Write($"Enter choice (1-{availableSlots.Count}): ");
                if (int.TryParse(Console.ReadLine(), out int val) && val >= 1 && val <= availableSlots.Count)
                {
                    slotChoice = val - 1;
                }
            }

            var chosen = availableSlots[slotChoice];
            return (chosen.FilePath, chosen.Name, chosen.Level, chosen.Deaths);
        }

        private static (string Name, int Level, int Deaths) ReadSaveData(string savePath)
        {
            byte[] fileBytes;
            using (var fs = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                fileBytes = ms.ToArray();
            }

            ArchiveFile archiveFile = ArchiveFile.Parser.ParseFrom(fileBytes);
            byte[] payloadBytes = archiveFile.GameArchivesDataBytes.ToByteArray();
            XorProcess(payloadBytes);

            FUStBEDArchivesData saveData = FUStBEDArchivesData.Parser.ParseFrom(payloadBytes);

            string playerName = saveData.RoleData?.RoleCs?.Base?.Name ?? "Unknown";
            int playerLevel = saveData.RoleData?.RoleCs?.Base?.Level ?? 0;
            int playerDeaths = saveData.PersistentECSData?.BGCData?.BGCPlayerDeathData?.PlayerDeathCount ?? 0;

            return (playerName, playerLevel, playerDeaths);
        }

        private static void PrintCard(string name, int level, int deaths)
        {
            Console.Clear();
            Console.WriteLine("================ CHARACTER DATA ================");
            Console.WriteLine($"Character Name:     {name}");
            Console.WriteLine($"Current Level:      {level}");
            Console.WriteLine($"Death Count:        {deaths}");
            Console.WriteLine("================================================");
            Console.WriteLine("OBS Overlay:        http://localhost:8080");
            Console.WriteLine("Recommended Size:   400 x 150");
        }

        private static void StartOverlayServer()
        {
            Task.Run(() =>
            {
                HttpListener? listener = null;
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add("http://localhost:8080/");
                    listener.Start();

                    while (listener.IsListening)
                    {
                        var context = listener.GetContext();
                        Task.Run(() => HandleHttpRequest(context));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Web server error: {ex.Message}");
                }
            });
        }

        private static void HandleHttpRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string path = request.Url?.AbsolutePath.ToLowerInvariant() ?? "/";

            try
            {
                if (path == "/" || path == "/index.html")
                {
                    byte[] htmlBytes = GetEmbeddedFileBytes("index.html");
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = htmlBytes.Length;
                    response.OutputStream.Write(htmlBytes, 0, htmlBytes.Length);
                    response.OutputStream.Close();
                }
                else if (path == "/1.ttf")
                {
                    byte[] fontBytes = GetEmbeddedFileBytes("1.ttf");
                    response.ContentType = "font/ttf";
                    response.ContentLength64 = fontBytes.Length;
                    response.OutputStream.Write(fontBytes, 0, fontBytes.Length);
                    response.OutputStream.Close();
                }
                else if (path == "/api/events")
                {
                    response.ContentType = "text/event-stream";
                    response.Headers.Add("Cache-Control", "no-cache");
                    response.Headers.Add("Connection", "keep-alive");
                    response.Headers.Add("Access-Control-Allow-Origin", "*");

                    var writer = new StreamWriter(response.OutputStream, Encoding.UTF8);

                    lock (ClientsLock)
                    {
                        SseClients.Add(writer);
                    }

                    try
                    {
                        if (_currentDeaths >= 0)
                        {
                            writer.Write($"data: {{\"deaths\": {_currentDeaths}}}\n\n");
                            writer.Flush();
                        }

                        while (true)
                        {
                            Thread.Sleep(15000);

                            writer.Write(": keepalive\n\n");
                            writer.Flush();
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        lock (ClientsLock)
                        {
                            SseClients.Remove(writer);
                        }

                        try { writer.Dispose(); } catch { }
                        try { response.Close(); } catch { }
                    }
                }
                else
                {
                    response.StatusCode = 404;
                    response.Close();
                }
            }
            catch
            {
            }
        }

        private static void BroadcastDeaths(int deaths)
        {
            string message = $"data: {{\"deaths\": {deaths}}}\n\n";

            lock (ClientsLock)
            {
                for (int i = SseClients.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        SseClients[i].Write(message);
                        SseClients[i].Flush();
                    }
                    catch
                    {
                        SseClients.RemoveAt(i);
                    }
                }
            }
        }

        private static byte[] GetEmbeddedFileBytes(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                if (File.Exists(fileName))
                {
                    return File.ReadAllBytes(fileName);
                }
                throw new FileNotFoundException($"Embedded resource file not found: {fileName}");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static void XorProcess(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= MagicKey[i % MagicKey.Length];
            }
        }
    }
}