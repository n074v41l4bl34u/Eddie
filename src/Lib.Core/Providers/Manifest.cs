﻿// <eddie_source_header>
// This file is part of Eddie/AirVPN software.
// Copyright (C)2014-2016 AirVPN (support@airvpn.org) / https://airvpn.org
//
// Eddie is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Eddie is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Eddie. If not, see <http://www.gnu.org/licenses/>.
// </eddie_source_header>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Eddie.Common;

namespace Eddie.Core.Providers
{
	public class Service : Core.Provider
	{
		public XmlNode Manifest;
		public XmlNode User;

		public List<ConnectionMode> Modes = new List<ConnectionMode>();

		private Int64 m_lastFetchTime = 0;
		private List<string> m_frontMessages = new List<string>();

		public override void OnInit()
		{
			base.OnInit();

#if (EDDIE3)
            Engine.Instance.Storage.SetDefaultBool("providers." + GetCode() + ".dns.check", true, Messages.ManOptionServicesDnsCheck);
            Engine.Instance.Storage.SetDefaultBool("providers." + GetCode() + ".tunnel.check", true, Messages.ManOptionServicesTunnelCheck);
#endif			
		}

		public override void OnLoad(XmlElement xmlStorage)
		{
			base.OnLoad(xmlStorage);

			CompatibilityManager.FixProviderStorage(Storage);

			Manifest = Storage.DocumentElement.SelectSingleNode("manifest");

			if (Manifest == null)
			{
				XmlNode nodeDefinitionDefaultManifest = Definition.SelectSingleNode("manifest");
				if (nodeDefinitionDefaultManifest == null)
					throw new Exception(Messages.ProvidersInvalid);

				Manifest = Storage.ImportNode(nodeDefinitionDefaultManifest, true);
				Storage.DocumentElement.AppendChild(Manifest);
			}

			User = Storage.DocumentElement.SelectSingleNode("user");
		}

		public override void OnBuildOvpnDefaults(OvpnBuilder ovpn)
		{
			base.OnBuildOvpnDefaults(ovpn);

			ovpn.AppendDirectives(Manifest.Attributes["openvpn_directives"].Value.Replace("\t", "").Trim(), "Provider level");
		}

		public override void OnBuildConnectionActive(ConnectionInfo connection, ConnectionActive connectionActive)
		{
			base.OnBuildConnectionActive(connection, connectionActive);

			OvpnBuilder ovpn = connectionActive.OpenVpnProfileStartup;
			ConnectionMode mode = GetMode();

			if (mode.Protocol == "SSH")
			{
				connectionActive.SshLocalPort = Engine.Instance.Storage.GetInt("ssh.port");
				connectionActive.SshRemotePort = mode.Port;
				connectionActive.SshPortDestination = mode.SshPortDestination;
				if (connectionActive.SshLocalPort == 0)
					connectionActive.SshLocalPort = RandomGenerator.GetInt(1024, 64 * 1024);
			}
			else if (mode.Protocol == "SSL")
			{
				connectionActive.SslLocalPort = Engine.Instance.Storage.GetInt("ssl.port");
				connectionActive.SslRemotePort = mode.Port;
				if (connectionActive.SslLocalPort == 0)
					connectionActive.SslLocalPort = RandomGenerator.GetInt(1024, 64 * 1024);
			}

			{
				string modeDirectives = mode.Directives;
				string paramUserTA = "";
				string paramUserTlsCrypt = "";
				if (User != null)
				{
					paramUserTA = UtilsXml.XmlGetAttributeString(User, "ta", "");
					paramUserTlsCrypt = UtilsXml.XmlGetAttributeString(User, "tls_crypt", "");
				}
				modeDirectives = modeDirectives.Replace("{@user-ta}", paramUserTA);
				modeDirectives = modeDirectives.Replace("{@user-tlscrypt}", paramUserTlsCrypt);
				ovpn.AppendDirectives(modeDirectives, "Mode level");
			}

			// Pick the IP
			IpAddress ip = null;
			string entryIpLayer = Engine.Instance.Storage.Get("network.entry.iplayer");
			if (entryIpLayer == "ipv6-ipv4")
			{
				ip = connection.IpsEntry.GetV6ByIndex(mode.EntryIndex);
				if (ip == null)
					ip = connection.IpsEntry.GetV4ByIndex(mode.EntryIndex);
			}
			else if (entryIpLayer == "ipv4-ipv6")
			{
				ip = connection.IpsEntry.GetV4ByIndex(mode.EntryIndex);
				if (ip == null)
					ip = connection.IpsEntry.GetV6ByIndex(mode.EntryIndex);
			}
			else if (entryIpLayer == "ipv6-only")
				ip = connection.IpsEntry.GetV6ByIndex(mode.EntryIndex);
			else if (entryIpLayer == "ipv4-only")
				ip = connection.IpsEntry.GetV4ByIndex(mode.EntryIndex);

			if (ip != null)
			{
				IpAddress remoteAddress = ip.Clone();
				int remotePort = mode.Port;

				if (mode.Protocol == "SSH")
				{
					remoteAddress = "127.0.0.1";
					remotePort = connectionActive.SshLocalPort;
				}
				else if (mode.Protocol == "SSL")
				{
					remoteAddress = "127.0.0.1";
					remotePort = connectionActive.SslLocalPort;
				}

				ovpn.AppendDirective("remote", remoteAddress.Address + " " + remotePort.ToString(), "");

				// Adjust the protocol
				OvpnBuilder.Directive dProto = ovpn.GetOneDirective("proto");
				if (dProto != null)
				{
					dProto.Text = dProto.Text.ToLowerInvariant();
					if (dProto.Text == "tcp")
					{
						if (remoteAddress.IsV6)
							dProto.Text = "tcp6";
					}
					else if (dProto.Text == "udp")
					{
						if (remoteAddress.IsV6)
							dProto.Text = "udp6";
					}
				}

				if ((mode.Protocol == "SSH") || (mode.Protocol == "SSL"))
				{
					if (Constants.FeatureIPv6ControlOptions)
					{
						if (((ip.IsV4) && (connectionActive.TunnelIPv4)) ||
							((ip.IsV6) && (connectionActive.TunnelIPv6)))
							connectionActive.AddRoute(ip, "net_gateway", "VPN Entry IP");
					}
					else
					{
						string routesDefault = Engine.Instance.Storage.Get("routes.default");
						if (routesDefault == "in")
						{
							connectionActive.AddRoute(ip, "net_gateway", "VPN Entry IP");
						}
					}
				}
			}

			connectionActive.Protocol = mode.Protocol;
			if (ip != null)
				connectionActive.Address = ip.Clone();
		}

		public override void OnBuildConnectionActiveAuth(ConnectionActive connectionActive)
		{
			base.OnBuildConnectionActiveAuth(connectionActive);

			string key = Engine.Instance.Storage.Get("key");

			XmlNode nodeUser = User;
			if (nodeUser != null)
			{
				connectionActive.OpenVpnProfileStartup.AppendDirective("<ca>", nodeUser.Attributes["ca"].Value, "");
				XmlElement xmlKey = nodeUser.SelectSingleNode("keys/key[@name=\"" + key.Replace("\"","") + "\"]") as XmlElement;
				if (xmlKey != null)
				{
					connectionActive.OpenVpnProfileStartup.AppendDirective("<cert>", xmlKey.Attributes["crt"].Value, "");
					connectionActive.OpenVpnProfileStartup.AppendDirective("<key>", xmlKey.Attributes["key"].Value, "");
				}
			}
		}

		public override void OnAuthFailed()
		{
			Engine.Instance.Logs.Log(LogType.Warning, Messages.AirVpnAuthFailed);
		}

		public override bool GetNeedRefresh()
		{
			int minInterval = RefreshInterval;
			{
				// Temp until option migration
				minInterval = Engine.Instance.Storage.GetInt("advanced.manifest.refresh");
				if (minInterval == 0)
					return false;
				if (minInterval != -1)
					minInterval *= 60;
			}
			if ((Manifest != null) && (minInterval == -1)) // Pick server recommended
			{
				minInterval = UtilsXml.XmlGetAttributeInt(Manifest, "next_update", -1);
				if (minInterval != -1)
					minInterval *= 60;
			}

			if (minInterval == -1)
				minInterval = 60 * 60 * 24;
			if (m_lastTryRefresh + minInterval > UtilsCore.UnixTimeStamp())
				return false;

			return true;
		}

		public override string OnRefresh()
		{
			base.OnRefresh();

			// Engine.Instance.Logs.LogVerbose(MessagesFormatter.Format(Messages.ProviderRefreshStart, Title));

			try
			{
				Dictionary<string, string> parameters = new Dictionary<string, string>();
				parameters["act"] = "manifest";
				parameters["ts"] = Conversions.ToString(m_lastFetchTime);
								
				XmlDocument xmlDoc = Fetch(MessagesFormatter.Format(Messages.ProviderRefreshStart, Title), parameters);
				lock (Storage)
				{
					if (Manifest != null)
						Storage.DocumentElement.RemoveChild(Manifest);

					Manifest = Storage.ImportNode(xmlDoc.DocumentElement, true);
					Storage.DocumentElement.AppendChild(Manifest);

					// Update with the local time
					Manifest.Attributes["time"].Value = UtilsCore.UnixTimeStamp().ToString();

					m_lastFetchTime = UtilsCore.UnixTimeStamp();
				}

				Engine.Instance.Logs.LogVerbose(MessagesFormatter.Format(Messages.ProviderRefreshDone, Title));

				string msg = GetFrontMessage();
				if ((msg != "") && (m_frontMessages.Contains(msg) == false))
				{
					Engine.Instance.OnFrontMessage(msg);
					m_frontMessages.Add(msg);
				}

				return "";
			}
			catch (Exception e)
			{
				Engine.Instance.Logs.LogVerbose(MessagesFormatter.Format(Messages.ProviderRefreshFail, Title, e.Message));

				return MessagesFormatter.Format(Messages.ProviderRefreshFail, Title, e.Message);
			}
		}

		public override IpAddresses GetNetworkLockAllowedIps()
		{
			IpAddresses result = base.GetNetworkLockAllowedIps();

			List<string> urls = GetBootstrapUrls();
			foreach (string url in urls)
			{
				string host = UtilsCore.HostFromUrl(url);
				if (host != "")
					result.Add(host);
			}

			return result;
		}

		public override string GetFrontMessage()
		{
			if (Manifest.Attributes["front_message"] != null)
			{
				string msg = Manifest.Attributes["front_message"].Value;
				return msg;
			}

			return base.GetFrontMessage();
		}

		public void Auth(XmlNode node)
		{
			lock (Storage)
			{
				if (User != null)
					Storage.DocumentElement.RemoveChild(User);

				User = Storage.ImportNode(node, true);
				Storage.DocumentElement.AppendChild(User);
			}
		}

		public void DeAuth()
		{
			lock (Storage)
			{
				if (User != null)
				{
					Storage.DocumentElement.RemoveChild(User);
					User = null;
				}

			}
		}

		public override void OnBuildConnections()
		{
			base.OnBuildConnections();

			lock (Manifest)
			{
				foreach (XmlNode nodeServer in Manifest.SelectNodes("//servers/server"))
				{
					string code = UtilsCore.HashSHA256(nodeServer.Attributes["name"].Value);

					ConnectionInfo infoServer = Engine.Instance.GetConnectionInfo(code, this);

					// Update info
					infoServer.DisplayName = TitleForDisplay + nodeServer.Attributes["name"].Value;
					infoServer.ProviderName = nodeServer.Attributes["name"].Value;
					infoServer.IpsEntry.Set(UtilsXml.XmlGetAttributeString(nodeServer, "ips_entry", ""));
					infoServer.IpsExit.Set(UtilsXml.XmlGetAttributeString(nodeServer, "ips_exit", ""));
					infoServer.CountryCode = UtilsXml.XmlGetAttributeString(nodeServer, "country_code", "");
					infoServer.Location = UtilsXml.XmlGetAttributeString(nodeServer, "location", "");
					infoServer.ScoreBase = UtilsXml.XmlGetAttributeInt64(nodeServer, "scorebase", 0);
					infoServer.Bandwidth = UtilsXml.XmlGetAttributeInt64(nodeServer, "bw", 0);
					infoServer.BandwidthMax = UtilsXml.XmlGetAttributeInt64(nodeServer, "bw_max", 1);
					infoServer.Users = UtilsXml.XmlGetAttributeInt64(nodeServer, "users", 0);
					infoServer.WarningOpen = UtilsXml.XmlGetAttributeString(nodeServer, "warning_open", "");
					infoServer.WarningClosed = UtilsXml.XmlGetAttributeString(nodeServer, "warning_closed", "");
					infoServer.SupportIPv4 = UtilsXml.XmlGetAttributeBool(nodeServer, "support_ipv4", false);
					infoServer.SupportIPv6 = UtilsXml.XmlGetAttributeBool(nodeServer, "support_ipv6", false);
					infoServer.SupportCheck = UtilsXml.XmlGetAttributeBool(nodeServer, "support_check", false);
					infoServer.OvpnDirectives = UtilsXml.XmlGetAttributeString(nodeServer, "openvpn_directives", "");
				}
			}

			RefreshModes();
		}

		public override void OnCheckConnections()
		{
			base.OnCheckConnections();

			ConnectionMode mode = GetMode();

			lock (Engine.Instance.Connections)
			{
				foreach (ConnectionInfo connection in Engine.Instance.Connections.Values)
				{
					if (connection.Provider != this)
						continue;

					if (User == null)
						connection.WarningAdd(Messages.ConnectionWarningLoginRequired, ConnectionInfoWarning.WarningType.Error);

					if (mode.EntryIndex >= connection.IpsEntry.CountIPv4)
						connection.WarningAdd(Messages.ConnectionWarningModeUnsupported, ConnectionInfoWarning.WarningType.Error);
				}
			}
		}

		public override string GetSshKey(string format)
		{
			return UtilsXml.XmlGetAttributeString(User, "ssh_" + format, "");
		}

		public override string GetSslCrt()
		{
			return UtilsXml.XmlGetAttributeString(User, "ssl_crt", "");
		}

		public bool SupportConnect
		{
			get
			{
				return true;
			}
		}

		public bool CheckDns
		{
			get
			{
#if (EDDIE3)
                return Engine.Instance.Storage.GetBool("providers." + GetCode() + ".dns.check");
#else
				return Engine.Instance.Storage.GetBool("dns.check");
#endif
			}
		}

		public bool CheckTunnel
		{
			get
			{
#if (EDDIE3)
                return Engine.Instance.Storage.GetBool("providers." + GetCode() + ".tunnel.check");
#else
				return Engine.Instance.Storage.GetBool("advanced.check.route");
#endif
			}
		}

		public void RefreshModes()
		{
			// Update modes
			lock (Modes)
			{
				Modes.Clear();
				foreach (XmlNode xmlMode in Manifest.SelectNodes("//modes/mode"))
				{
					ConnectionMode mode = new ConnectionMode();
					mode.ReadXML(xmlMode as XmlElement);
					Modes.Add(mode);
				}
			}
		}

		public List<string> GetBootstrapUrls()
		{
			List<string> urls = new List<string>();

			// Manual Urls
			foreach (string url in Engine.Instance.Storage.Get("bootstrap.urls").Split(';'))
			{
				string sUrl = url.Trim();
				if (sUrl != "")
				{
					if (IpAddress.IsIP(sUrl))
						sUrl = "http://" + sUrl;
					string host = UtilsCore.HostFromUrl(sUrl);
					if (host != "")
						urls.Add(sUrl);
				}
			}

			// Manifest Urls
			if (Manifest != null)
			{
				XmlNodeList nodesUrls = Manifest.SelectNodes("//urls/url");
				foreach (XmlNode nodeUrl in nodesUrls)
				{
					urls.Add(nodeUrl.Attributes["address"].Value);
				}
			}

			return urls;
		}

		public XmlDocument Fetch(string title, Dictionary<string, string> parameters)
		{
			List<string> urls = GetBootstrapUrls();

			string authPublicKey = Manifest.SelectSingleNode("rsa").InnerXml;

			return FetchUrls(title, authPublicKey, urls, parameters);
		}

		// This is the only method about exchange data between this software and AirVPN infrastructure.
		// We don't use SSL. Useless layer in our case, and we need to fetch hostname and direct IP that don't permit common-name match.

		// 'S' is the AES 256 bit one-time session key, crypted with a RSA 4096 public-key.
		// 'D' is the data from the client to our server, crypted with the AES.
		// The server answer is XML decrypted with the same AES session.
		public XmlDocument FetchUrl(string authPublicKey, string url, Dictionary<string, string> parameters)
		{
			// AES				
			using (RijndaelManaged rijAlg = new RijndaelManaged())
			{
				rijAlg.KeySize = 256;
				rijAlg.GenerateKey();
				rijAlg.GenerateIV();

				// Generate S

				// Bug workaround: Xamarin 6.1.2 macOS throw an 'Default constructor not found for type System.Diagnostics.FilterElement' error.
				// in 'new System.Xml.Serialization.XmlSerializer', so i avoid that.
				/*
				StringReader sr = new System.IO.StringReader(authPublicKey);
				System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(RSAParameters));
				RSAParameters publicKey = (RSAParameters)xs.Deserialize(sr);
				*/
				RSAParameters publicKey = new RSAParameters();
				XmlDocument docAuthPublicKey = new XmlDocument();
				docAuthPublicKey.LoadXml(authPublicKey);
				publicKey.Modulus = Convert.FromBase64String(docAuthPublicKey.DocumentElement["Modulus"].InnerText);
				publicKey.Exponent = Convert.FromBase64String(docAuthPublicKey.DocumentElement["Exponent"].InnerText);

				Dictionary<string, byte[]> assocParamS = new Dictionary<string, byte[]>();
				assocParamS["key"] = rijAlg.Key;
				assocParamS["iv"] = rijAlg.IV;

				byte[] bytesParamS = null;
				using (RSACryptoServiceProvider csp = new RSACryptoServiceProvider())
				{
					csp.ImportParameters(publicKey);
					bytesParamS = csp.Encrypt(UtilsCore.AssocToUtf8Bytes(assocParamS), false);
				}

				// Generate D

				byte[] aesDataIn = UtilsCore.AssocToUtf8Bytes(parameters);
				byte[] bytesParamD = null;

				{
					MemoryStream aesCryptStream = null;
					CryptoStream aesCryptStream2 = null;

					try
					{
						aesCryptStream = new MemoryStream();
						using (ICryptoTransform aesEncryptor = rijAlg.CreateEncryptor())
						{
							aesCryptStream2 = new CryptoStream(aesCryptStream, aesEncryptor, CryptoStreamMode.Write);
							aesCryptStream2.Write(aesDataIn, 0, aesDataIn.Length);
							aesCryptStream2.FlushFinalBlock();

							bytesParamD = aesCryptStream.ToArray();
						}
					}
					finally
					{
						if (aesCryptStream2 != null)
							aesCryptStream2.Dispose();
						else if (aesCryptStream != null)
							aesCryptStream.Dispose();
					}
				}

				// HTTP Fetch
				HttpRequest request = new HttpRequest();
				request.Url = url;
				request.Parameters["s"] = UtilsString.Base64Encode(bytesParamS);
				request.Parameters["d"] = UtilsString.Base64Encode(bytesParamD);

				HttpResponse response = Engine.Instance.FetchUrl(request);

				try
				{
					byte[] fetchResponse = response.BufferData;
					byte[] fetchResponsePlain = null;

					MemoryStream aesDecryptStream = null;
					CryptoStream aesDecryptStream2 = null;

					// Decrypt answer

					try
					{
						aesDecryptStream = new MemoryStream();
						using (ICryptoTransform aesDecryptor = rijAlg.CreateDecryptor())
						{
							aesDecryptStream2 = new CryptoStream(aesDecryptStream, aesDecryptor, CryptoStreamMode.Write);
							aesDecryptStream2.Write(fetchResponse, 0, fetchResponse.Length);
							aesDecryptStream2.FlushFinalBlock();

							fetchResponsePlain = aesDecryptStream.ToArray();
						}
					}
					finally
					{
						if (aesDecryptStream2 != null)
							aesDecryptStream2.Dispose();
						else if (aesDecryptStream != null)
							aesDecryptStream.Dispose();
					}

					string finalData = System.Text.Encoding.UTF8.GetString(fetchResponsePlain);

					XmlDocument doc = new XmlDocument();
					doc.LoadXml(finalData);
					return doc;
				}
				catch (Exception ex)
				{
					string message = "";
					if (response.GetHeader("location") != "")
						message = MessagesFormatter.Format(Messages.ProviderRefreshFailUnexpected302, Title, response.GetHeader("location"));
					else
						message = ex.Message + " - " + response.GetLineReport();
					throw new Exception(message);
				}
			}
		}

		public XmlDocument FetchUrls(string title, string authPublicKey, List<string> urls, Dictionary<string, string> parameters)
		{
			parameters["login"] = Engine.Instance.Storage.Get("login");
			parameters["password"] = Engine.Instance.Storage.Get("password");
			parameters["system"] = Platform.Instance.GetSystemCode();
			parameters["version"] = Constants.VersionInt.ToString(CultureInfo.InvariantCulture);

			string firstError = "";
			int hostN = 0;
			foreach (string url in urls)
			{
				string host = UtilsCore.HostFromUrl(url);

				hostN++;
				if (IpAddress.IsIP(host) == false)
				{
					// If locked network are enabled, skip the hostname and try only by IP.
					// To avoid DNS issue (generally, to avoid losing time).
					if (Engine.Instance.NetworkLockManager.IsDnsResolutionAvailable(host) == false)
						continue;
				}

				try
				{
					RouteScope routeScope = new RouteScope(host);
					XmlDocument xmlDoc = FetchUrl(authPublicKey, url, parameters);
					routeScope.End();
					if (xmlDoc == null)
						throw new Exception("No answer.");

					if (xmlDoc.DocumentElement.Attributes["error"] != null)
						throw new Exception(xmlDoc.DocumentElement.Attributes["error"].Value);

					return xmlDoc;
				}
				catch (Exception e)
				{
					string info = e.Message;
					string proxyMode = Engine.Instance.Storage.Get("proxy.mode").ToLowerInvariant();
					string proxyWhen = Engine.Instance.Storage.Get("proxy.when").ToLowerInvariant();
					string proxyAuth = Engine.Instance.Storage.Get("proxy.auth").ToLowerInvariant();
					if (proxyMode != "none")
						info += " - with '" + proxyMode + "' (" + proxyWhen + ") proxy and '" + proxyAuth + "' auth";

					if (Engine.Instance.Storage.GetBool("advanced.expert"))
						Engine.Instance.Logs.Log(LogType.Verbose, MessagesFormatter.Format(Messages.ExchangeTryFailed, title, hostN.ToString(), info));

					if (firstError == "")
						firstError = info;
				}
			}

			throw new Exception(firstError);
		}

		public ConnectionMode GetModeAuto()
		{
			string proxyMode = Engine.Instance.Storage.GetLower("proxy.mode");

			foreach (ConnectionMode mode in Modes)
			{
				if (mode.Available == false)
					continue;
				if ((proxyMode != "none") && (mode.Protocol != "TCP"))
					continue;

				return mode;
			}

			return null;
		}

		public ConnectionMode GetMode()
		{
			String protocol = Engine.Instance.Storage.Get("mode.protocol").ToUpperInvariant();
			int port = Engine.Instance.Storage.GetInt("mode.port");
			int entry = Engine.Instance.Storage.GetInt("mode.alt");

			if (protocol == "AUTO")
			{
				return GetModeAuto();
			}
			else
			{
				foreach (ConnectionMode mode in Modes)
				{
					if ((mode.Protocol.ToLowerInvariant() == protocol.ToLowerInvariant()) &&
						(mode.Port == port) &&
						(mode.EntryIndex == entry))
						return mode;
				}
			}

			return GetModeAuto();
		}
	}
}
