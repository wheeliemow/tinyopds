using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Xml;
using System.IO;
using System.Net.NetworkInformation;
using System.ComponentModel;
using Microsoft.Win32;

namespace UPnP
{
    public class UPnPController : IDisposable
    {
        private bool _disposed = false;
        private string _descUrl, _serviceUrl, _eventUrl;
        private BackgroundWorker _worker;
        private WebClient _webClient;

        public bool Discovered { get; private set; }
        public event EventHandler DiscoverCompleted;

        public bool UPnPReady { get { return !string.IsNullOrEmpty(_serviceUrl); } }

        public UPnPController ()
        {
            LocalIP = DetectLocalIP();
            Discovered = false;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this._disposed && disposing)
            {
                if (_webClient != null && _webClient.IsBusy)
                {
                    _webClient.CancelAsync();
                    _webClient.Dispose();
                }
                DiscoverCompleted = null;
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void DiscoverAsync(bool useUPnP)
        {
            _worker = new BackgroundWorker();
            _worker.DoWork += new DoWorkEventHandler(_worker_DoWork);
            _worker.RunWorkerAsync(useUPnP);
        }

        void _worker_DoWork(object sender, DoWorkEventArgs e)
        {
            bool detectUPnP = (bool) e.Argument;
            if (detectUPnP)
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.ReceiveTimeout = 2000;
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1);
                    string req = "M-SEARCH * HTTP/1.1\r\n" +
                                 "HOST: 239.255.255.250:1900\r\n" +
                                 "ST:upnp:rootdevice\r\n" +
                                 "MAN:\"ssdp:discover\"\r\n" +
                                 "MX:3\r\n\r\n";
                    byte[] data = Encoding.ASCII.GetBytes(req);
                    IPEndPoint ipe = new IPEndPoint(IPAddress.Broadcast, 1900);
                    byte[] buffer = new byte[0x1000];

                    for (int i = 0; i < 2; i++) socket.SendTo(data, ipe);

                    int length = 0;
                    do
                    {
                        try { length = socket.Receive(buffer); }
                        catch { break; }
                        string resp = Encoding.ASCII.GetString(buffer, 0, length).ToLower();
                        if (resp.Contains("upnp:rootdevice"))
                        {
                            resp = resp.Substring(resp.ToLower().IndexOf("location:") + 9);
                            resp = resp.Substring(0, resp.IndexOf("\r")).Trim();
                            if (!string.IsNullOrEmpty(_serviceUrl = GetServiceUrl(resp)))
                            {
                                _descUrl = resp;
                                break;
                            }
                        }
                    } while (length > 0);
                }
                Discovered = true;

                string ip = "127.0.0.0";
                XmlDocument xdoc = SOAPRequest(_serviceUrl,
                    "<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                    "</u:GetExternalIPAddress>", "GetExternalIPAddress");
                if (xdoc.OuterXml.Contains("NewExternalIPAddress"))
                {
                    XmlNamespaceManager nsMgr = new XmlNamespaceManager(xdoc.NameTable);
                    nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
                    ip = xdoc.SelectSingleNode("//NewExternalIPAddress/text()", nsMgr).Value;
                }
                ExternalIP = IPAddress.Parse(ip);
                if (DiscoverCompleted != null) DiscoverCompleted(this, new EventArgs());
            }
            // Just detect external IP address
            else
            {
                _webClient = new WebClient();
                _webClient.DownloadStringCompleted += (object o, DownloadStringCompletedEventArgs ea) =>
                    {
                        if (!_disposed)
                        {
                            ExternalIP = IPAddress.Parse(ea.Result);
                            if (DiscoverCompleted != null) DiscoverCompleted(this, new EventArgs());
                        }
                    };
                _webClient.DownloadStringAsync(new Uri("http://myip.dnsdynamic.org")); 
            }
        }

        private string GetServiceUrl(string resp)
        {
            // Prevent IOException 
            // See https://connect.microsoft.com/VisualStudio/feedback/details/773666/webrequest-create-eats-an-ioexception-on-the-first-call#details
            RegistryKey registryKey = null;
            registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Wow6432Node\\Microsoft\\.NETFramework", true);
            if (registryKey == null) registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\.NETFramework", true);
            if (registryKey.GetValue("LegacyWPADSupport") == null) registryKey.SetValue("LegacyWPADSupport", 0);

            try
            {
                XmlDocument desc = new XmlDocument();
                desc.Load(WebRequest.Create(resp).GetResponse().GetResponseStream());
                XmlNamespaceManager nsMgr = new XmlNamespaceManager(desc.NameTable);
                nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
                XmlNode typen = desc.SelectSingleNode("//tns:device/tns:deviceType/text()", nsMgr);
                if (!typen.Value.Contains("InternetGatewayDevice")) return null;
                XmlNode node = desc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:WANIPConnection:1\"]/tns:controlURL/text()", nsMgr);
                if (node == null) return null;
                XmlNode eventnode = desc.SelectSingleNode("//tns:service[tns:serviceType=\"urn:schemas-upnp-org:service:WANIPConnection:1\"]/tns:eventSubURL/text()", nsMgr);
                _eventUrl = CombineUrls(resp, eventnode.Value);
                return CombineUrls(resp, node.Value);
            }
            catch 
            { 
                return null; 
            }
        }

        private string CombineUrls(string resp, string p)
        {
            int n = resp.IndexOf("://");
            n = resp.IndexOf('/', n + 3);
            return resp.Substring(0, n) + p;
        }

        public void ForwardPort(int port, ProtocolType protocol, string description)
        {
            if (UPnPReady)
            {
                XmlDocument xdoc = SOAPRequest(_serviceUrl, 
                    "<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                    "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port.ToString() + "</NewExternalPort><NewProtocol>" + protocol.ToString().ToUpper() + "</NewProtocol>" +
                    "<NewInternalPort>" + port.ToString() + "</NewInternalPort><NewInternalClient>" + LocalIP.ToString() +
                    "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>" + description +
                    "</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration></u:AddPortMapping>", "AddPortMapping");
            }
        }

        public void DeleteForwardingRule(int port, ProtocolType protocol)
        {
            if (UPnPReady)
            {
                XmlDocument xdoc = SOAPRequest(_serviceUrl,
                    "<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                    "<NewRemoteHost>" +
                    "</NewRemoteHost>" +
                    "<NewExternalPort>" + port + "</NewExternalPort>" +
                    "<NewProtocol>" + protocol.ToString().ToUpper() + "</NewProtocol>" +
                    "</u:DeletePortMapping>", "DeletePortMapping");
            }
        }

        /// <summary>
        /// Local IP address
        /// </summary>
        /// <returns></returns>
        public IPAddress LocalIP { get; private set; }

        private IPAddress DetectLocalIP()
        {
            IPAddress address = IPAddress.Any;
            try
            {
                string ip = "127.0.0.0";
                foreach (NetworkInterface networkCard in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (GatewayIPAddressInformation gatewayAddr in networkCard.GetIPProperties().GatewayAddresses)
                    {
                        if (gatewayAddr.Address.ToString() != "0.0.0.0")
                        {
                            ip = gatewayAddr.Address.ToString();
                            ip = ip.Substring(0, ip.LastIndexOf('.'));
                            break;
                        }
                    }
                }

                IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
                foreach (IPAddress addr in addresses)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        if (addr.ToString().Contains(ip))
                        {
                            address = addr;
                            break;
                        }
                    }
                }
            }
            catch { }
            return address;
        }

        public IPAddress ExternalIP { get; private set;}

        private static XmlDocument SOAPRequest(string url, string soap, string function)
        {
            XmlDocument resp = new XmlDocument();
            try
            {
                string req = "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body>" +
                soap +
                "</s:Body>" +
                "</s:Envelope>";
                WebRequest r = HttpWebRequest.Create(url);
                r.Method = "POST";
                byte[] b = Encoding.UTF8.GetBytes(req);
                r.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#" + function + "\"");
                r.ContentType = "text/xml; charset=\"utf-8\"";
                r.ContentLength = b.Length;
                r.GetRequestStream().Write(b, 0, b.Length);
                WebResponse wres = r.GetResponse();
                Stream ress = wres.GetResponseStream();
                resp.Load(ress);
            }
            catch { }
            return resp;
        }
    }
}
