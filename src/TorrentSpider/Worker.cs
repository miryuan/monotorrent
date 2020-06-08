using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonoTorrent.Client;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht;
using System.Net;
using System.Text;

namespace TorrentSpider
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private string dhtNodeFile;
        private string basePath;
        private string downloadsPath;
        private string fastResumeFile;
        private string torrentsPath;
        ClientEngine engine;				// �������ص�����
        private List<TorrentManager> torrents;	// The list where all the torrentManagers will be stored that the engine gives us
        private Top10Listener listener;			// This is a subclass of TraceListener which remembers the last 20 statements sent to it

        public Worker (ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync (CancellationToken stoppingToken)
        {
            basePath = Environment.CurrentDirectory;						// �������ڵ�Ŀ¼
            torrentsPath = Path.Combine (basePath, "Torrents");				// Torrent�洢��Ŀ¼
            downloadsPath = Path.Combine (basePath, "Downloads");			// ָ�����ص�Ŀ¼
            fastResumeFile = Path.Combine (torrentsPath, "fastresume.data");
            dhtNodeFile = Path.Combine (basePath, "DhtNodes");
            torrents = new List<TorrentManager> ();							// �洢TorrentManager�б�
            listener = new Top10Listener (10);

            StartEngine ().Wait ();

            while (!stoppingToken.IsCancellationRequested) {
                await Task.Delay (1000, stoppingToken);
            }
        }

        private async Task StartEngine ()
        {
            int port = 5200;
            Torrent torrent = null;
            // Ask the user what port they want to use for incoming connections
            // ѯ���û�Ҫ��ʹ���ĸ��˿���������
            //Console.Write ($"{Environment.NewLine} ѡ������Ķ˿�: ");
            //while (!Int32.TryParse (Console.ReadLine (), out port)) { }

            // ����һ�������Ĭ������
            // downloadsPath - �ļ����ص�Ŀ¼
            // port - ��������Ķ˿�
            EngineSettings engineSettings = new EngineSettings {
                SavePath = downloadsPath,
                ListenPort = port
            };

            //engineSettings.GlobalMaxUploadSpeed = 30 * 1024;
            //engineSettings.GlobalMaxDownloadSpeed = 100 * 1024;
            //engineSettings.MaxReadRate = 1 * 1024 * 1024;

            // ����һ�� Torrent Ĭ�ϵ�������Ϣ.
            TorrentSettings torrentDefaults = new TorrentSettings ();

            // ����һ���ͻ�������.
            engine = new ClientEngine (engineSettings);

            byte[] nodes = Array.Empty<byte> ();
            try {
                if (File.Exists (dhtNodeFile))
                    nodes = File.ReadAllBytes (dhtNodeFile);
            } catch {
                Console.WriteLine ("�޷������κ�����DHT�ڵ�.");
            }

            DhtEngine dht = new DhtEngine (new IPEndPoint (IPAddress.Any, port));
            await engine.RegisterDhtAsync (dht);

            // �⽫����Dht����,������ȴ���ȫ��ʼ�����.
            // ������Ϊ�������ӽڵ�ʱ��ʱ������,���������Ҫ2����.
            await engine.DhtEngine.StartAsync (nodes);

            // �������·��������,�򴴽�.
            if (!Directory.Exists (engine.Settings.SavePath))
                Directory.CreateDirectory (engine.Settings.SavePath);

            // ���Torrent�洢Ŀ¼������,�򴴽�.
            if (!Directory.Exists (torrentsPath))
                Directory.CreateDirectory (torrentsPath);

            BEncodedDictionary fastResume = new BEncodedDictionary ();
            try {
                if (File.Exists (fastResumeFile))
                    fastResume = BEncodedValue.Decode<BEncodedDictionary> (File.ReadAllBytes (fastResumeFile));
            } catch {
            }

            // ��TorrentsĿ¼�е�ÿ�� torrent �ļ�������ص�������.
            foreach (string file in Directory.GetFiles (torrentsPath)) {
                if (file.EndsWith (".torrent", StringComparison.OrdinalIgnoreCase)) {
                    try {
                        // ����torrent�ļ���Torrentʵ����,�����Ҫ�Ļ�,����ʹ��������Ԥ����
                        torrent = await Torrent.LoadAsync (file);
                        Console.WriteLine (torrent.InfoHash.ToString ());
                    } catch (Exception e) {
                        Console.Write ("Couldn't decode {0}: ", file);
                        Console.WriteLine (e.Message);
                        continue;
                    }
                    // ���κ�Ԥ������ɺ�,��������һ��TorrentManager,Ȼ���������ϴ�����.
                    TorrentManager manager = new TorrentManager (torrent, downloadsPath, torrentDefaults);
                    if (fastResume.ContainsKey (torrent.InfoHash.ToHex ()))
                        manager.LoadFastResume (new FastResume ((BEncodedDictionary) fastResume[torrent.InfoHash.ToHex ()]));
                    await engine.Register (manager);

                    // �� TorrentManager �洢���б���,�����Ժ������.
                    torrents.Add (manager);
                    manager.PeersFound += Manager_PeersFound;
                }
            }

            // If we loaded no torrents, just exist. The user can put files in the torrents directory and start the client again
            if (torrents.Count == 0) {
                Console.WriteLine ("û����Ŀ¼���ҵ� torrent �ļ�");
                Console.WriteLine ("�˳�...");
                engine.Dispose ();
                return;
            }

            // �����洢���б��е�ÿ��TorrentManager,��TorrentManager�����ӵ��¼����������档
            foreach (TorrentManager manager in torrents) {
                manager.PeerConnected += (o, e) => {
                    lock (listener)
                        listener.WriteLine ($"���ӳɹ�: {e.Peer.Uri}");
                };
                manager.ConnectionAttemptFailed += (o, e) => {
                    lock (listener)
                        listener.WriteLine (
                            $"����ʧ��: {e.Peer.ConnectionUri} - {e.Reason} - {e.Peer.AllowedEncryption}");
                };
                // ÿ��ɢ��һ��Ƭ��,�ͻᴥ�����.
                manager.PieceHashed += delegate (object o, PieceHashedEventArgs e) {
                    lock (listener)
                        listener.WriteLine ($"ɢ�е�Ƭ��: {e.PieceIndex} - {(e.HashPassed ? "Pass" : "Fail")}");
                };

                // ÿ��״̬�ı�ʱ���� (Stopped -> Seeding -> Downloading -> Hashing)
                manager.TorrentStateChanged += delegate (object o, TorrentStateChangedEventArgs e) {
                    lock (listener)
                        listener.WriteLine ($"��״̬: {e.OldState} ��״̬: {e.NewState}");
                };

                // ÿ����������״̬�ı�ʱ,�ͻᴥ��.
                manager.TrackerManager.AnnounceComplete += (sender, e) => {
                    listener.WriteLine ($"{e.Successful}: {e.Tracker}");
                };

                // ��ʼ����TorrentManager.
                // Ȼ���ļ���ɢ��(�����Ҫ)����ʼ����/����.
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

                    AppendFormat (sb, "�������ٶ�: {0:0.00}kB/sec", engine.TotalDownloadSpeed / 1024.0);
                    AppendFormat (sb, "���ϴ��ٶ�:   {0:0.00}kB/sec", engine.TotalUploadSpeed / 1024.0);
                    AppendFormat (sb, "���̶��ٶ�:      {0:0.00} kB/s", engine.DiskManager.ReadRate / 1024.0);
                    AppendFormat (sb, "����д�ٶ�:     {0:0.00} kB/s", engine.DiskManager.WriteRate / 1024.0);
                    AppendFormat (sb, "Total Read:         {0:0.00} kB", engine.DiskManager.TotalRead / 1024.0);
                    AppendFormat (sb, "Total Written:      {0:0.00} kB", engine.DiskManager.TotalWritten / 1024.0);
                    AppendFormat (sb, "Open Connections:    {0}", engine.ConnectionManager.OpenConnections);

                    foreach (TorrentManager manager in torrents) {
                        AppendSeparator (sb);
                        AppendFormat (sb, "״̬:           {0}", manager.State);
                        AppendFormat (sb, "����:            {0}", manager.Torrent == null ? "MetaDataMode" : manager.Torrent.Name);
                        AppendFormat (sb, "����:           {0:0.00}", manager.Progress);
                        AppendFormat (sb, "�����ٶ�:     {0:0.00} kB/s", manager.Monitor.DownloadSpeed / 1024.0);
                        AppendFormat (sb, "�ϴ��ٶ�:       {0:0.00} kB/s", manager.Monitor.UploadSpeed / 1024.0);
                        AppendFormat (sb, "������:   {0:0.00} MB", manager.Monitor.DataBytesDownloaded / (1024.0 * 1024.0));
                        AppendFormat (sb, "�ϴ���:     {0:0.00} MB", manager.Monitor.DataBytesUploaded / (1024.0 * 1024.0));
                        AppendFormat (sb, "Tracker Status");
                        foreach (var tier in manager.TrackerManager.Tiers)
                            AppendFormat (sb, $"\t{tier.ActiveTracker} : Announce Succeeded: {tier.LastAnnounceSucceeded}. Scrape Succeeded: {tier.LastScrapSucceeded}.");
                        if (manager.PieceManager != null)
                            AppendFormat (sb, "��ǰ����:   {0}", await manager.PieceManager.CurrentRequestCountAsync ());

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

            // ֹͣ������uPnP��NAT-PMP���ݵ��豸,��ɾ�������Ѵ�����ӳ��.
            await engine.DisablePortForwardingAsync (CancellationToken.None);
        }

        private void Manager_PeersFound (object sender, PeersAddedEventArgs e)
        {
            lock (listener)
                listener.WriteLine ($"�ҵ� {e.NewPeers} λ�µĹ�����,�� {e.ExistingPeers} λ���������˳�.");//throw new Exception("The method or operation is not implemented.");
        }

        private void AppendSeparator (StringBuilder sb)
        {
            AppendFormat (sb, "", null);
            AppendFormat (sb, "- - - - - - - - - - - - - - - - - - - - - - - - - - - - - -", null);
            AppendFormat (sb, "", null);
        }

        private void AppendFormat (StringBuilder sb, string str, params object[] formatting)
        {
            if (formatting != null)
                sb.AppendFormat (str, formatting);
            else
                sb.Append (str);
            sb.AppendLine ();
        }
    }
}
