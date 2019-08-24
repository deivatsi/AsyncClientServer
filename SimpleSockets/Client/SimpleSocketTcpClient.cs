using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimpleSockets.Messaging;
using SimpleSockets.Messaging.MessageContract;
using SimpleSockets.Messaging.Metadata;

namespace SimpleSockets.Client
{
	public class SimpleSocketTcpClient: SimpleSocketClient
	{

		/// <summary>
		/// Creates a TcpClient socket.
		/// </summary>
		public SimpleSocketTcpClient() : base()
		{

		}

		/// <summary>
		/// Starts the client.
		/// <para>requires server ip, port number and how many seconds the client should wait to try to connect again. Default is 5 seconds</para>
		/// </summary>
		public override void StartClient(string ipServer, int port, int reconnectInSeconds = 5)
		{

			if (Disposed)
				return;

			if (string.IsNullOrEmpty(ipServer))
				throw new ArgumentNullException(nameof(ipServer));
			if (port < 1 || port > 65535)
				throw new ArgumentOutOfRangeException(nameof(port));
			if (reconnectInSeconds < 3)
				throw new ArgumentOutOfRangeException(nameof(reconnectInSeconds));


			Ip = ipServer;
			Port = port;
			ReconnectInSeconds = reconnectInSeconds;
			KeepAliveTimer.Enabled = false;

			if (EnableExtendedAuth)
				SendAuthMessage();

			Endpoint = new IPEndPoint(GetIp(ipServer), port);

			TokenSource = new CancellationTokenSource();
			Token = TokenSource.Token;

			Task.Run(SendFromQueue, Token);

			Task.Run(() =>
			{
				try
				{
					if (Token.IsCancellationRequested || Disposed)
						return;

					//Try and connect
					Listener = new Socket(Endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
					Listener.BeginConnect(Endpoint, this.OnConnectCallback, Listener);
					ConnectedMre.WaitOne();

					//If client is connected activate connected event
					if (IsConnected())
					{
						RaiseConnected();
					}
					else
					{
						KeepAliveTimer.Enabled = false;
						RaiseDisconnected();
						Close();
						ConnectedMre.Reset();
						Listener.BeginConnect(Endpoint, this.OnConnectCallback, Listener);
					}

				}
				catch (Exception ex)
				{
					throw new Exception(ex.Message, ex);
				}
			}, Token);


		}
		
		protected override void OnConnectCallback(IAsyncResult result)
		{
			if (Disposed)
				return;

			var server = (Socket)result.AsyncState;

			try
			{
				//Client is connected to server and set connected variable
				server.EndConnect(result);
				ConnectedMre.Set();
				KeepAliveTimer.Enabled = true;
				var state = new ClientMetadata(Listener);
				NetworkDataReceiver(state);
			}
			catch (ObjectDisposedException ex)
			{
				RaiseErrorThrown(ex);
			}
			catch (SocketException)
			{
				Thread.Sleep(ReconnectInSeconds * 1000);
				if (!Token.IsCancellationRequested && !Disposed)
					Listener.BeginConnect(Endpoint, OnConnectCallback, Listener);
			}
			catch (Exception ex)
			{
				throw new Exception(ex.Message, ex);
			}
		}

		#region Sending

		protected override void BeginSendFromQueue(MessageWrapper message)
		{
			try
			{
				Listener.BeginSend(message.Data, 0, message.Data.Length, SocketFlags.None, SendCallback, message);
			}
			catch (Exception ex)
			{
				RaiseErrorThrown(ex);
			}
		}

		//Send message and invokes MessageSubmitted.
		protected override void SendCallback(IAsyncResult result)
		{
			var message = (MessageWrapper)result.AsyncState;
			try
			{
				Listener.EndSend(result);
			}
			catch (SocketException se)
			{
				throw new Exception(se.ToString());
			}
			catch (ObjectDisposedException se)
			{
				throw new Exception(se.ToString());
			}
			finally
			{
				if (!message.Partial)
					RaiseMessageSubmitted(CloseClient);

				if (!message.Partial && CloseClient)
					Close();

				SentMre.Set();
			}
		}

		protected override void SendToSocket(byte[] data, bool close, bool partial = false, int id = -1)
		{
			CloseClient = close;
			BlockingMessageQueue.Enqueue(new MessageWrapper(data, partial));
		}


		#endregion


		#region Receiving

		protected override void NetworkDataReceiver(IClientMetadata state)
		{
			try
			{
				while (!Token.IsCancellationRequested)
				{
					var offset = 0;

					if (state.UnhandledBytes != null && state.UnhandledBytes.Length > 0)
						offset = state.UnhandledBytes.Length;

					if (state.Buffer.Length < state.BufferSize)
					{
						state.ChangeBuffer(new byte[state.BufferSize]);
						if (offset > 0)
							Array.Copy(state.UnhandledBytes, 0, state.Buffer, 0, state.UnhandledBytes.Length);
					}

					state.Listener.BeginReceive(state.Buffer, offset, state.Buffer.Length - offset, SocketFlags.None,
						Receiver = async (ar) =>
						{
							if (!IsConnected())
							{
								RaiseLog(new Exception("Socket is not connected, can't receive messages."));
								return;
							}

							var client = (ClientMetadata)ar.AsyncState;
							var receive = client.Listener.EndReceive(ar);

							if (client.UnhandledBytes != null && client.UnhandledBytes.Length > 0)
							{
								receive += client.UnhandledBytes.Length;
								client.UnhandledBytes = null;
							}

							//Does header check
							if (state.Flag == 0)
							{
								if (client.SimpleMessage == null)
									client.SimpleMessage = new SimpleMessage(client, this, true);
								await client.SimpleMessage.ReadBytesAndBuildMessage(receive);
							}
							else if (receive > 0)
							{
								await client.SimpleMessage.ReadBytesAndBuildMessage(receive);
							}
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
		
	}
}
