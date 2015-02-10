﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Xml;
using System.Xml.Linq;
using SharpXMPP.XMPP;
using SharpXMPP.XMPP.Bind;
using SharpXMPP.XMPP.Bind.Elements;
using SharpXMPP.XMPP.Client;
using SharpXMPP.XMPP.Client.Disco;
using SharpXMPP.XMPP.Client.Disco.Elements;
using SharpXMPP.XMPP.Client.Elements;
using SharpXMPP.XMPP.SASL;
using SharpXMPP.XMPP.SASL.Elements;
using SharpXMPP.XMPP.Stream.Elements;
using SharpXMPP.XMPP.TLS.Elements;
using System.Threading;

namespace SharpXMPP
{
    public abstract class XmppTcpConnection : XmppConnection
    {

        private readonly TcpClient _client;

        protected virtual int TcpPort
        {
            get { return 5222; }
            set { throw new NotImplementedException(); }
        }

        protected virtual IEnumerable<IPAddress> HostAddresses
        {
            get
            {
                var addresses = new List<IPAddress>();
                DNS.ResolveXMPPClient(Jid.Domain).ForEach(d => addresses.AddRange(Dns.GetHostAddresses(d.Host)));
                return addresses;
            }
            set { throw new NotImplementedException(); }
        }
    
        protected readonly string Password;

        
    
        protected XmppTcpConnection(string ns, JID jid, string password) : base(ns)
        {
            Jid = jid;
            
            Password = password;	    
	        _client = new TcpClient();
	        _client.Connect(HostAddresses.ToArray(), TcpPort); // TODO: check ports
	        ConnectionStream = _client.GetStream();
            Iq += (sender, iq) => new XMPP.Client.IqManager(this)
            {
                PayloadHandlers = new List<PayloadHandler>
                          {
                              new InfoHandler(Capabilities),
                              new ItemsHandler()
                          }
            }.Handle(iq);
        }

        public System.IO.Stream ConnectionStream;

        protected XmlReader Reader;
        protected XmlWriter Writer;

        protected void RestartXmlStreams()
        {
            var xws = new XmlWriterSettings { ConformanceLevel = ConformanceLevel.Fragment, OmitXmlDeclaration = true };
            Writer = XmlWriter.Create(ConnectionStream, xws);
            OpenXmppStream();
            var xrs = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment };
            Reader = XmlReader.Create(ConnectionStream, xrs);

        }

        protected void OpenXmppStream()
        {
            Writer.WriteStartElement("stream", "stream", Namespaces.Streams);
            Writer.WriteAttributeString("xmlns", Namespace);
            Writer.WriteAttributeString("version", "1.0");
            Writer.WriteAttributeString("to", Jid.Domain);
            Writer.WriteRaw("");
            Writer.Flush();
        }

        public override XElement NextElement()
        {
            Reader.MoveToContent();
            if (Reader.LocalName.Equals("stream") && Reader.NamespaceURI.Equals(Namespaces.Streams))
            {
                OnStreamStart(Reader.GetAttribute("id"));
            }
            do
            {
                Reader.Read();
            } while (Reader.NodeType != XmlNodeType.Element);
            var result = XElement.Load(Reader.ReadSubtree());
            OnElement(new ElementArgs { Stanza = result, IsInput = true });
            return result;
        }

        public override void Send(XElement data)
        {
            base.Send(data);
            data.WriteTo(Writer);
            Writer.WriteRaw("");
            Writer.Flush();
        }

        public void Close()
        {
            Writer.WriteEndElement();
        }

        public override void SessionLoop()
        {
            while (true)
            {
                try
                {
                    var el = NextElement();
                    if (el.Name.LocalName.Equals("iq"))
                    {
                        OnIq(Stanza.Parse<XMPPIq>(el));
                    }
                    if (el.Name.LocalName.Equals("message"))
                    {
                        OnMessage(Stanza.Parse<XMPPMessage>(el));
                    }

                }
                catch (Exception e)
                {
                    OnConnectionFailed(new ConnFailedArgs { Message = e.Message });
                    break;
                }
            }
        }

        public override void Connect()
        {
            RestartXmlStreams();
            var features = Stanza.Parse<Features>(NextElement());
            if (features.Tls)
            {
                Send(new StartTLS());
                var res = Stanza.Parse<Proceed>(NextElement());
                if (res != null)
                {
                    ConnectionStream = new SslStream(ConnectionStream, true);
                    ((SslStream)ConnectionStream).AuthenticateAsClient(Jid.Domain);
                    RestartXmlStreams();
                    NextElement();
                }
            }

            var authenticator = SASLHandler.Create(features.SaslMechanisms, Jid, Password);
            if (authenticator == null)
            {
                OnConnectionFailed(new ConnFailedArgs { Message = "supported sasl mechanism not available" });
                return;
            }
            authenticator.Authenticated += sender =>
            {
                RestartXmlStreams();
                var session = new SessionHandler();
                session.SessionStarted += connection => OnSignedIn(new SignedInArgs {Jid = connection.Jid});
                session.Start(this);
            };
            authenticator.Start(this);
        }
    }
}
