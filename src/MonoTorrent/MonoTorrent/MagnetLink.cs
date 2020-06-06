//
// MagnetLink.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2006 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Text;

namespace MonoTorrent
{
    /// <summary>
    /// 磁力链接
    /// </summary>
    public class MagnetLink
    {
        /// <summary>
        /// The list of tracker Urls.
        /// </summary>
        public RawTrackerTier AnnounceUrls {
            get;
        }

        /// <summary>
        /// The infohash of the torrent.
        /// </summary>
        public InfoHash InfoHash {
            get;
        }

        /// <summary>
        /// 数据的字节量.
        /// </summary>
        public long? Size {
            get;
        }

        /// <summary>
        /// 对应的种子名称.
        /// </summary>
        public string Name {
            get;
        }

        /// <summary>
        /// The list of webseed Urls.
        /// </summary>
        public IList<string> Webseeds {
            get;
        }

        public MagnetLink (InfoHash infoHash, string name = null, IList<string> announceUrls = null, IEnumerable<string> webSeeds = null, long? size = null)
        {
            InfoHash = infoHash ?? throw new ArgumentNullException (nameof (infoHash));
            Name = name;
            AnnounceUrls = new RawTrackerTier (announceUrls ?? Array.Empty<string> ());
            Webseeds = new List<string> (webSeeds ?? Array.Empty<string> ()).AsReadOnly ();
            Size = size;
        }

        /// <summary>
        /// 将磁力链接字符串转换成磁力链接对象. uri必须为[magnet:?xt=urn:btih:]开头的字符串
        /// </summary>
        /// <param name="uri">以[magnet:?xt=urn:btih:]开头的字符串</param>
        /// <returns></returns>
        public static MagnetLink Parse (string uri)
        {
            return FromUri (new Uri (uri));
        }

        /// <summary>
        /// 从给定的Uri解析磁铁链接. uri必须为[magnet:?xt=urn:btih:]开头的Uri对象
        /// </summary>
        /// <param name="uri">以[magnet:?xt=urn:btih:]开头的Uri对象</param>
        /// <returns></returns>
        public static MagnetLink FromUri (Uri uri)
        {
            InfoHash infoHash = null;
            string name = null;
            var announceUrls = new RawTrackerTier ();
            var webSeeds = new List<string> ();
            long? size = null;

            if (uri.Scheme != "magnet")
                throw new FormatException ("磁力链接必须为'magnet:'开头.");

            string[] parameters = uri.Query.Substring (1).Split ('&');
            for (int i = 0; i < parameters.Length; i++) {
                string[] keyval = parameters[i].Split ('=');
                if (keyval.Length != 2) {
                    // Skip anything we don't understand. Urls could theoretically contain many
                    // unknown parameters.
                    continue;
                }
                switch (keyval[0].Substring (0, 2)) {
                    case "xt"://exact topic
                        if (infoHash != null)
                            throw new FormatException ("磁铁链接中不允许有多个infohash.");

                        string val = keyval[1].Substring (9);
                        switch (keyval[1].Substring (0, 9)) {
                            case "urn:sha1:"://base32 hash
                            case "urn:btih:":
                                if (val.Length == 32)
                                    infoHash = InfoHash.FromBase32 (val);
                                else if (val.Length == 40)
                                    infoHash = InfoHash.FromHex (val);
                                else
                                    throw new FormatException ("Infohash必须是Base32或十六进制编码.");
                                break;
                        }
                        break;
                    case "tr"://address tracker
                        announceUrls.Add (keyval[1].UrlDecodeUTF8 ());
                        break;
                    case "as"://Acceptable Source
                        webSeeds.Add (keyval[1].UrlDecodeUTF8 ());
                        break;
                    case "dn"://display name
                        name = keyval[1].UrlDecodeUTF8 ();
                        break;
                    case "xl"://exact length
                        size = long.Parse (keyval[1]);
                        break;
                    //case "xs":// eXact Source - P2P link.
                    //case "kt"://keyword topic
                    //case "mt"://manifest topic
                    // Unused
                    //break;
                    default:
                        // Unknown/unsupported
                        break;
                }
            }

            if (infoHash == null)
                throw new FormatException ("磁铁链接不包含引用infohash的有效\"xt\"参数");

            return new MagnetLink (infoHash, name, announceUrls, webSeeds, size);
        }

        public string ToV1String ()
        {
            return ConvertToString ();
        }

        public Uri ToV1Uri ()
        {
            return new Uri (ToV1String ());
        }

        string ConvertToString ()
        {
            var sb = new StringBuilder ();
            sb.Append ("magnet:?");
            sb.Append ("xt=urn:btih:");
            sb.Append (InfoHash.ToHex ());

            if (!string.IsNullOrEmpty (Name)) {
                sb.Append ("&dn=");
                sb.Append (Name.UrlEncodeUTF8 ());
            }

            foreach (string tracker in AnnounceUrls) {
                sb.Append ("&tr=");
                sb.Append (tracker.UrlEncodeUTF8 ());
            }

            foreach (string webseed in Webseeds) {
                sb.Append ("&as=");
                sb.Append (webseed.UrlEncodeUTF8 ());
            }

            return sb.ToString ();
        }
    }
}