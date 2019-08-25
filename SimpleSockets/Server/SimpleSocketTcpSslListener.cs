using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimpleSockets.Messaging;
using SimpleSockets.Messaging.Metadata;

namespace SimpleSockets.Server
{

	public class SimpleSocketTcpSslListener : SimpleSocketListener
	{

		#region Vars

		private readonly X509Certificate2 _serverCertificate = null;
		private readonly TlsProtocol _tlsProtocol;

		public bool AcceptInvalidCertificates { get; set; }
		public bool MutualAuthentication { get; set; }

		#endregion

		#region Constructor

		/// <summary>
		/// Creates a new SSL listener.
		/// cert = is the path of the certificate.
		/// certPass = is the password of the certificate.
		/// </summary>
		/// <param name="cert"></param>
		/// <param name="certPass"></param>
		/// <param name="tlsProtocol"></param>
		/// <param name="acceptInvalidCertificates"></param>
		public SimpleSocketTcpSslListener(string cert, string certPass, TlsProtocol tlsProtocol = TlsProtocol.Tls12, bool acceptInvalidCertificates = true, bool mutualAuth = false) : base()
		{
			if (string.IsNullOrEmpty(cert))
				throw new ArgumentNullException(nameof(cert));

			AcceptInvalidCertificates = acceptInvalidCertificates;
			MutualAuthentication = mutualAuth;
			_tlsProtocol = tlsProtocol;

			if (string.IsNullOrEmpty(certPass))
				_serverCertificate = new X509Certificate2(File.ReadAllBytes(Path.GetFullPath(cert)));
			else
				_serverCertificate = new X509Certificate2(File.ReadAllBytes(Path.GetFullPath(cert)), certPass);
		}

		#endregion

		#region StartServer

		/// <summary>
		/// Starts the server.
		/// </summary>
		/// <param name="ip"></param>
		/// <param name="port"></param>
		/// <param name="limit"></param>
		public override void StartListening(string ip, int port, int limit = 500)
		{
			if (port < 1)
				throw new ArgumentOutOfRangeException(nameof(port));
			if (limit < 0)
				throw new ArgumentException("Limit cannot be under 0.");
			if (limit == 0)
				throw new ArgumentException("Limit cannot be 0.");


			Port = port;
			Ip = ip;

			var endpoint = new IPEndPoint(DetermineListenerIp(ip), port);

			TokenSource = new CancellationTokenSource();
			Token = TokenSource.Token;

			Task.Run(SendFromQueue, Token);

			Task.Run(() =>
			{
				try
				{
					using (var listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
					{
						Listener = listener;
						listener.Bind(endpoint);
						listener.Listen(Limit);

						RaiseServerHasStarted();
						while (!Token.IsCancellationRequested)
						{
							CanAcceptConnections.Reset();
							listener.BeginAccept(OnClientConnect, listener);
							CanAcceptConnections.WaitOne();
						}
					}
				}
				catch (SocketException se)
				{
					throw new Exception(se.ToString());
				}
			}, Token);
		}

		protected override void OnClientConnect(IAsyncResult result)
		{
			CanAcceptConnections.Set();
			try
			{
				IClientMetadata state;
				int id;

				lock (ConnectedClients)
				{
					id = !ConnectedClients.Any() ? 1 : ConnectedClients.Keys.Max() + 1;

					state = new ClientMetadata(((Socket)result.AsyncState).EndAccept(result), id);


					ConnectedClients.Add(id, state);
				}

				//If the server shouldn't accept the IP do nothing.
				if (!IsConnectionAllowed(state))
					return;

				var stream = new NetworkStream(state.Listener);

				//Create SslStream
				state.SslStream = new SslStream(stream, false, AcceptCertificate);

				Task.Run(() =>
				{
					var success = Authenticate(state).Result;

					if (success)
					{
						RaiseClientConnected(state);
						NetworkDataReceiver(state);
					}
					else
					{

						lock (ConnectedClients)
						{
							ConnectedClients.Remove(id);
						}

						throw new AuthenticationException("Unable to authenticate server.");
					}

				}, new CancellationTokenSource(10000).Token);


			}
			catch (Exception ex)
			{
				RaiseLog(ex);
				RaiseErrorThrown(ex);
			}
		}

		#endregion

		#region Ssl Auth

		private bool AcceptCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicy)
		{
			return !AcceptInvalidCertificates ? _serverCertificate.Verify() : AcceptInvalidCertificates;
		}

		private async Task<bool> Authenticate(IClientMetadata state)
		{
			try
			{
				SslProtocols protocol;

				switch (_tlsProtocol)
				{
					case TlsProtocol.Tls10:
						protocol = SslProtocols.Tls;
						break;
					case TlsProtocol.Tls11:
						protocol = SslProtocols.Tls11;
						break;
					case TlsProtocol.Tls12:
						protocol = SslProtocols.Tls12;
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}

				await state.SslStream.AuthenticateAsServerAsync(_serverCertificate, true, protocol, false);

				if (!state.SslStream.IsEncrypted)
				{
					throw new Exception("Stream from client " + state.Id + " is not encrypted.");
				}

				if (!state.SslStream.IsAuthenticated)
				{
					throw new Exception("Stream from client " + state.Id + " not authenticated.");
				}

				if (MutualAuthentication && !state.SslStream.IsMutuallyAuthenticated)
				{
					throw new AuthenticationException("Failed to mutually authenticate.");
				}

				RaiseAuthSuccess(state);
				return true;
			}
			catch (Exception ex)
			{
				RaiseAuthFailed(state);
				RaiseErrorThrown(ex);
				RaiseLog("Failed to authenticate ssl certificate.");
				return false;
			}
		}

		#endregion

		#region Sending

		protected override void BeginSendFromQueue(MessageWrapper message)
		{
			try
			{
				message.State.MreWrite.WaitOne();
				message.State.MreWrite.Reset();
				message.State.SslStream.BeginWrite(message.Data, 0, message.Data.Length, SendCallback, message);
			}
			catch (Exception ex)
			{
				RaiseLog("A message failed to send to client with ip: " + message.State.RemoteIPv4);
				RaiseMessageFailed(message.State, message.Data, ex);
			}
		}

		protected override void SendCallback(IAsyncResult result)
		{
			var message = (MessageWrapper)result.AsyncState;
			var state = message.State;

			try
			{
				state.SslStream.EndWrite(result);
				if (!message.Partial && state.Close)
					Close(state.Id);
			}
			catch (SocketException se)
			{
				throw new SocketException(se.ErrorCode);
			}
			catch (ObjectDisposedException ode)
			{
				throw new ObjectDisposedException(ode.ObjectName, ode.Message);
			}
			catch (Exception ex)
			{
				throw new Exception(ex.Message, ex);
			}
			finally
			{
				if (!message.Partial)
					RaiseMessageSubmitted(state, state.Close);

				message.State.MreWrite.Set();
				message.Dispose();
			}
		}

		protected override void SendToSocket(byte[] payLoad, bool close, bool partial = false, int id = -1)
		{
			var state = GetClient(id);

			try
			{
				if (state == null)
					throw new Exception("Client does not exist.");

				if (!IsConnected(id))
				{
					RaiseClientDisconnected(state);
					Close(id);
					throw new Exception("Message failed to send because the destination socket is not connected.");
				}

				state.Close = close;
				BlockingMessageQueue.Enqueue(new MessageWrapper(payLoad, state, partial));
			}
			catch (Exception ex)
			{
				RaiseMessageFailed(state, payLoad, ex);
			}
		}

		#endregion

		#region Receive

		protected override void NetworkDataReceiver(IClientMetadata state)
		{
			try
			{
				var offset = 0;
				while (!Token.IsCancellationRequested)
				{
					state.MreRead.WaitOne();
					state.MreRead.Reset();

					if (offset > 0)
					{
						state.UnhandledBytes = state.Buffer;
					}

					if (state.Buffer.Length < state.BufferSize || offset > 0)
					{
						state.ChangeBuffer(new byte[state.BufferSize]);
						if (offset > 0)
							Array.Copy(state.UnhandledBytes, 0, state.Buffer, 0, state.UnhandledBytes.Length);
					}

					state.SslStream.BeginRead(state.Buffer, offset, state.Buffer.Length - offset,
						Receiver = async (ar) =>
						{
							var client = (ClientMetadata)ar.AsyncState;
							var receive = client.SslStream.EndRead(ar);

							if (receive > 0)
							{

								if (!IsConnected(client.Id))
								{
									RaiseClientDisconnected(client);
									lock (ConnectedClients)
									{
										ConnectedClients.Remove(client.Id);
									}
								}
								//Else start receiving and handle the message.
								else
								{

									if (client.UnhandledBytes != null && client.UnhandledBytes.Length > 0)
									{
										receive += client.UnhandledBytes.Length;
										client.UnhandledBytes = null;
									}

									if (client.Flag == 0 && receive > 0)
									{
										if (client.SimpleMessage == null)
											client.SimpleMessage = new SimpleMessage(client, this, Debug);
										await ParallelQueue.Enqueue(() => client.SimpleMessage.ReadBytesAndBuildMessage(receive));
									}
									else if (receive > 0)
										await ParallelQueue.Enqueue(() => client.SimpleMessage.ReadBytesAndBuildMessage(receive));
								}
								offset = client.Buffer.Length;
							}
							else
								offset = 0;

							client.MreRead.Set();
							state = client;
						}, state);

				}
			}
			catch (Exception ex)
			{
				state.Reset();
				RaiseLog(ex);
				RaiseLog("Error handling message from client with guid : " + state.Guid + ".");
				RaiseErrorThrown(ex);
				NetworkDataReceiver(state);
			}
		}

		#endregion

		/// <summary>
		/// Disposes of the AsyncSocketSslListener.
		/// </summary>
		public override void Dispose()
		{
			try
			{
				base.Dispose();
			}
			catch (Exception ex)
			{
				throw new Exception("Error trying to dispose of " + nameof(SimpleSocketTcpSslListener) + " class.", ex);
			}

		}

	}
}
