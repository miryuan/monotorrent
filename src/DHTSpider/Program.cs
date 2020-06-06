using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Dht;

namespace DHTSpider
{
    class Program
    {
        static string dhtNodeFile;
        static string basePath;
        static string downloadsPath;
        static string fastResumeFile;
        static string torrentsPath;
        static ClientEngine engine;				// 用于下载的引擎
        static List<TorrentManager> torrents;	// The list where all the torrentManagers will be stored that the engine gives us
        static Top10Listener listener;			// This is a subclass of TraceListener which remembers the last 20 statements sent to it

        static void Main (string[] args)
        {
            /* Generate the paths to the folder we will save .torrent files to and where we download files to */
            basePath = Environment.CurrentDirectory;						// 程序所在的目录
            torrentsPath = Path.Combine (basePath, "Torrents");				// Torrent存储的目录
            downloadsPath = Path.Combine (basePath, "Downloads");			// 指定下载的目录
            fastResumeFile = Path.Combine (torrentsPath, "fastresume.data");
            dhtNodeFile = Path.Combine (basePath, "DhtNodes");
            torrents = new List<TorrentManager> ();							// 存储TorrentManager列表
            listener = new Top10Listener (10);

            // 当用户使用 Ctrl - C 关闭窗口或发生未处理的异常时，我们需要正确处理
            Console.CancelKeyPress += delegate { Shutdown ().Wait (); };
            AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown ().Wait (); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine (e.ExceptionObject); Shutdown ().Wait (); };
            Thread.GetDomain ().UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine (e.ExceptionObject); Shutdown ().Wait (); };

            StartEngine ().Wait ();

            while (true) {
                Thread.Sleep (5000);
            }
        }

        private static async Task StartEngine ()
        {
            int port;
            Torrent torrent = null;
            // Ask the user what port they want to use for incoming connections
            // 询问用户要将使用哪个端口用于连接
            Console.Write ($"{Environment.NewLine} 选择监听的端口: ");
            while (!Int32.TryParse (Console.ReadLine (), out port)) { }

            // 创建一个引擎的默认配置
            // downloadsPath - 文件下载的目录
            // port - 引擎监听的端口
            EngineSettings engineSettings = new EngineSettings {
                SavePath = downloadsPath,
                ListenPort = port
            };

            //engineSettings.GlobalMaxUploadSpeed = 30 * 1024;
            //engineSettings.GlobalMaxDownloadSpeed = 100 * 1024;
            //engineSettings.MaxReadRate = 1 * 1024 * 1024;

            // 创建一个 Torrent 默认的配置信息.
            TorrentSettings torrentDefaults = new TorrentSettings ();

            // 创建一个客户端引擎.
            engine = new ClientEngine (engineSettings);

            byte[] nodes = Array.Empty<byte> ();
            try {
                if (File.Exists (dhtNodeFile))
                    nodes = File.ReadAllBytes (dhtNodeFile);
            } catch {
                Console.WriteLine ("No existing dht nodes could be loaded");
            }

            DhtEngine dht = new DhtEngine (new IPEndPoint (IPAddress.Any, port));
            await engine.RegisterDhtAsync (dht);

            // 这将启动Dht引擎,但不会等待完全初始化完成.
            // 这是因为根据连接节点时超时的数量,启动最多需要2分钟.
            await engine.DhtEngine.StartAsync (nodes);

            // 如果下载路径不存在,则创建之.
            if (!Directory.Exists (engine.Settings.SavePath))
                Directory.CreateDirectory (engine.Settings.SavePath);

            // 如果Torrent存储目录不存在,则创建之.
            if (!Directory.Exists (torrentsPath))
                Directory.CreateDirectory (torrentsPath);

            BEncodedDictionary fastResume = new BEncodedDictionary ();
            try {
                if (File.Exists (fastResumeFile))
                    fastResume = BEncodedValue.Decode<BEncodedDictionary> (File.ReadAllBytes (fastResumeFile));
            } catch {
            }

            // 将Torrents目录中的每个 torrent 文件将其加载到引擎中.
            foreach (string file in Directory.GetFiles (torrentsPath)) {
                if (file.EndsWith (".torrent", StringComparison.OrdinalIgnoreCase)) {
                    try {
                        // 加载torrent文件到Torrent实例中,如果需要的话,可以使用它进行预处理
                        torrent = await Torrent.LoadAsync (file);
                        Console.WriteLine (torrent.InfoHash.ToString ());
                    } catch (Exception e) {
                        Console.Write ("Couldn't decode {0}: ", file);
                        Console.WriteLine (e.Message);
                        continue;
                    }
                    // 当任何预处理完成后,您将创建一个TorrentManager,然后在引擎上创建它.
                    TorrentManager manager = new TorrentManager (torrent, downloadsPath, torrentDefaults);
                    if (fastResume.ContainsKey (torrent.InfoHash.ToHex ()))
                        manager.LoadFastResume (new FastResume ((BEncodedDictionary) fastResume[torrent.InfoHash.ToHex ()]));
                    await engine.Register (manager);

                    // 将 TorrentManager 存储在列表中,方便以后访问它.
                    torrents.Add (manager);
                    manager.PeersFound += Manager_PeersFound;
                }
            }

            // If we loaded no torrents, just exist. The user can put files in the torrents directory and start the client again
            if (torrents.Count == 0) {
                Console.WriteLine ("没有在目录中找到 torrent 文件");
                Console.WriteLine ("退出...");
                engine.Dispose ();
                return;
            }

            // 遍历存储在列表中的每个TorrentManager,在TorrentManager中连接到事件并启动引擎。
            foreach (TorrentManager manager in torrents) {
                manager.PeerConnected += (o, e) => {
                    lock (listener)
                        listener.WriteLine ($"连接成功: {e.Peer.Uri}");
                };
                manager.ConnectionAttemptFailed += (o, e) => {
                    lock (listener)
                        listener.WriteLine (
                            $"连接失败: {e.Peer.ConnectionUri} - {e.Reason} - {e.Peer.AllowedEncryption}");
                };
                // 每次散列一个片段,就会触发这个.
                manager.PieceHashed += delegate (object o, PieceHashedEventArgs e) {
                    lock (listener)
                        listener.WriteLine ($"散列的片段: {e.PieceIndex} - {(e.HashPassed ? "Pass" : "Fail")}");
                };

                // 每当状态改变时触发 (Stopped -> Seeding -> Downloading -> Hashing)
                manager.TorrentStateChanged += delegate (object o, TorrentStateChangedEventArgs e) {
                    lock (listener)
                        listener.WriteLine ($"旧状态: {e.OldState} 新状态: {e.NewState}");
                };

                // 每当跟踪器的状态改变时,就会触发.
                manager.TrackerManager.AnnounceComplete += (sender, e) => {
                    listener.WriteLine ($"{e.Successful}: {e.Tracker}");
                };

                // 开始运行TorrentManager.
                // 然后文件将散列(如果需要)并开始下载/发送.
                await manager.StartAsync ();
            }

            // Enable automatic port forwarding. The engine will use Mono.Nat to search for
            // uPnP or NAT-PMP compatible devices and then issue port forwarding requests to it.
            await engine.EnablePortForwardingAsync (CancellationToken.None);

            // This is how to access the list of port mappings, and to see if they were
            // successful, pending or failed. If they failed it could be because the public port
            // is already in use by another computer on your network.
            foreach (var successfulMapping in engine.PortMappings.Created) { }
            foreach (var failedMapping in engine.PortMappings.Failed) { }
            foreach (var failedMapping in engine.PortMappings.Pending) { }

            // While the torrents are still running, print out some stats to the screen.
            // Details for all the loaded torrent managers are shown.
            int i = 0;
            bool running = true;
            StringBuilder sb = new StringBuilder (1024);
            while (running) {
                if ((i++) % 10 == 0) {
                    sb.Remove (0, sb.Length);
                    running = torrents.Exists (m => m.State != TorrentState.Stopped);

                    AppendFormat (sb, "Total Download Rate: {0:0.00}kB/sec", engine.TotalDownloadSpeed / 1024.0);
                    AppendFormat (sb, "Total Upload Rate:   {0:0.00}kB/sec", engine.TotalUploadSpeed / 1024.0);
                    AppendFormat (sb, "Disk Read Rate:      {0:0.00} kB/s", engine.DiskManager.ReadRate / 1024.0);
                    AppendFormat (sb, "Disk Write Rate:     {0:0.00} kB/s", engine.DiskManager.WriteRate / 1024.0);
                    AppendFormat (sb, "Total Read:         {0:0.00} kB", engine.DiskManager.TotalRead / 1024.0);
                    AppendFormat (sb, "Total Written:      {0:0.00} kB", engine.DiskManager.TotalWritten / 1024.0);
                    AppendFormat (sb, "Open Connections:    {0}", engine.ConnectionManager.OpenConnections);

                    foreach (TorrentManager manager in torrents) {
                        AppendSeparator (sb);
                        AppendFormat (sb, "State:           {0}", manager.State);
                        AppendFormat (sb, "Name:            {0}", manager.Torrent == null ? "MetaDataMode" : manager.Torrent.Name);
                        AppendFormat (sb, "Progress:           {0:0.00}", manager.Progress);
                        AppendFormat (sb, "Download Speed:     {0:0.00} kB/s", manager.Monitor.DownloadSpeed / 1024.0);
                        AppendFormat (sb, "Upload Speed:       {0:0.00} kB/s", manager.Monitor.UploadSpeed / 1024.0);
                        AppendFormat (sb, "Total Downloaded:   {0:0.00} MB", manager.Monitor.DataBytesDownloaded / (1024.0 * 1024.0));
                        AppendFormat (sb, "Total Uploaded:     {0:0.00} MB", manager.Monitor.DataBytesUploaded / (1024.0 * 1024.0));
                        AppendFormat (sb, "Tracker Status");
                        foreach (var tier in manager.TrackerManager.Tiers)
                            AppendFormat (sb, $"\t{tier.ActiveTracker} : Announce Succeeded: {tier.LastAnnounceSucceeded}. Scrape Succeeded: {tier.LastScrapSucceeded}.");
                        if (manager.PieceManager != null)
                            AppendFormat (sb, "Current Requests:   {0}", await manager.PieceManager.CurrentRequestCountAsync ());

                        foreach (PeerId p in await manager.GetPeersAsync ())
                            AppendFormat (sb, "\t{2} - {1:0.00}/{3:0.00}kB/sec - {0}", p.Uri,
                                                                                      p.Monitor.DownloadSpeed / 1024.0,
                                                                                      p.AmRequestingPiecesCount,
                                                                                      p.Monitor.UploadSpeed / 1024.0);

                        AppendFormat (sb, "", null);
                        if (manager.Torrent != null)
                            foreach (TorrentFile file in manager.Torrent.Files)
                                AppendFormat (sb, "{1:0.00}% - {0}", file.Path, file.BitField.PercentComplete);
                    }
                    Console.Clear ();
                    Console.WriteLine (sb.ToString ());
                    listener.ExportTo (Console.Out);
                }

                Thread.Sleep (500);
            }

            // 停止搜索与uPnP或NAT-PMP兼容的设备,并删除所有已创建的映射.
            await engine.DisablePortForwardingAsync (CancellationToken.None);
        }

        static void Manager_PeersFound (object sender, PeersAddedEventArgs e)
        {
            lock (listener)
                listener.WriteLine ($"Found {e.NewPeers} new peers and {e.ExistingPeers} existing peers");//throw new Exception("The method or operation is not implemented.");
        }

        private static void AppendSeparator (StringBuilder sb)
        {
            AppendFormat (sb, "", null);
            AppendFormat (sb, "- - - - - - - - - - - - - - - - - - - - - - - - - - - - - -", null);
            AppendFormat (sb, "", null);
        }

        private static void AppendFormat (StringBuilder sb, string str, params object[] formatting)
        {
            if (formatting != null)
                sb.AppendFormat (str, formatting);
            else
                sb.Append (str);
            sb.AppendLine ();
        }

        private static async Task Shutdown ()
        {
            BEncodedDictionary fastResume = new BEncodedDictionary ();
            for (int i = 0; i < torrents.Count; i++) {
                var stoppingTask = torrents[i].StopAsync ();
                while (torrents[i].State != TorrentState.Stopped) {
                    Console.WriteLine ("{0} is {1}", torrents[i].Torrent.Name, torrents[i].State);
                    Thread.Sleep (250);
                }
                await stoppingTask;

                if (torrents[i].HashChecked)
                    fastResume.Add (torrents[i].Torrent.InfoHash.ToHex (), torrents[i].SaveFastResume ().Encode ());
            }

            var nodes = await engine.DhtEngine.SaveNodesAsync ();
            File.WriteAllBytes (dhtNodeFile, nodes);
            File.WriteAllBytes (fastResumeFile, fastResume.Encode ());
            engine.Dispose ();

            Thread.Sleep (2000);
        }
    }
}
