﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AsyncClientServer.Client;
using AsyncClientServer.Server;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AsyncClientServer.Example.Client
{
	/// <summary>
	/// Interaction logic for Client.xaml
	/// </summary>
	public partial class Client : Window
	{

		private readonly SocketClient _client;

		public Client()
		{
			InitializeComponent();

			//_client = new AsyncSocketSslClient(@"", "");

			try
			{
				_client = new AsyncSocketClient();
				_client.AllowReceivingFiles = true;
				BindEvents();
				Task.Run(() => StartClient());
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message);
			}
		}

		private void StartClient()
		{
			_client.StartClient("127.0.0.1", 13000);
		}

		private void BindEvents()
		{
			_client.ProgressFileReceived += Progress;
			_client.ConnectedToServer += ConnectedToServer;
			_client.ClientErrorThrown += ErrorThrown;
			_client.MessageReceived += ServerMessageReceived;
			_client.MessageSubmitted += ClientMessageSubmitted;
			_client.FileReceived += FileReceived;
			_client.DisconnectedFromServer += Disconnected;
			_client.MessageFailed += MessageFailed;
			_client.CustomHeaderReceived += CustomHeader;
		}

		//Converts DateTime to a string according to cultureInfo. (uses CurrentCulture.)
		private static string ConvertDateTimeToString(DateTime time)
		{
			var cultureInfo = CultureInfo.CurrentCulture;
			//CultureInfo us = new CultureInfo("en-US");
			var shortDateFormatString = cultureInfo.DateTimeFormat.ShortDatePattern;
			var longTimeFormatString = cultureInfo.DateTimeFormat.LongTimePattern;

			return time.ToString(shortDateFormatString + " " + longTimeFormatString, cultureInfo);

		}

		private void AppendRichtTextBoxLog(string text)
		{
			Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
				new Action(() =>
				{
					RichTextBoxLogs.AppendText(Environment.NewLine + "[" + ConvertDateTimeToString(DateTime.Now) + "] " + text);
				}));			
		}

		private void ChangeStatus(string text)
		{
			Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
				new Action(() => { TextBlockStatus.Text = text; }));
		}

		private void CustomHeader(SocketClient a, string msg, string header)
		{
			AppendRichtTextBoxLog(header + ": " + msg);
		}


		private void ErrorThrown(SocketClient socketClient, Exception error)
		{
			AppendRichtTextBoxLog("Error: " + error.Message);
		}

		private void ConnectedToServer(SocketClient a)
		{
			AppendRichtTextBoxLog("The client has connected to the server.");
			ChangeStatus("CONNECTED");
			MessageBox.Show("Client has connected to the server on ip : " + a.Ip);
		}

		private void ServerMessageReceived(SocketClient a, string msg)
		{
			AppendRichtTextBoxLog("MESSAGE" + ": " + msg);
		}

		void FileReceived(SocketClient a, string file)
		{
			AppendRichtTextBoxLog("File/Folder has been received and saved at path: " + file);
		}

		void Disconnected(SocketClient a, string ip, int port)
		{
			AppendRichtTextBoxLog("Client has disconnected from the server.");
			ChangeStatus("DISCONNECTED");
			MessageBox.Show("Client has disconnected to the server on ip : " + ip + " on port " + port);
		}

		void Progress(SocketClient a, int bytes, int messageSize)
		{
		}

		void ClientMessageSubmitted(SocketClient a, bool close)
		{
			AppendRichtTextBoxLog("Client has submitted a message.");
		}

		private void MessageFailed(SocketClient tcpClient, byte[] messageData, Exception exception)
		{
		}


		//Messaging

		//Message
		private void ButtonMessage_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				string message = new TextRange(RichTextBoxMessage.Document.ContentStart, RichTextBoxMessage.Document.ContentEnd).Text;
				_client.SendMessage(message, false);
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}


		}




		//Command 
		private void ButtonCommand_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				string msg = new TextRange(RichTextBoxCommand.Document.ContentStart, RichTextBoxCommand.Document.ContentEnd).Text;
				string header = TextBoxCustomHeaderHeader.Text;
				_client.SendCustomHeaderMessage(msg, header, false);
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}


		}


		//Folder
		private void ButtonChooseFolder_Click(object sender, RoutedEventArgs e)
		{
			FolderBrowserDialog dialog = new FolderBrowserDialog();
			DialogResult result = dialog.ShowDialog();

			if (result == System.Windows.Forms.DialogResult.OK)
			{
				TextBoxFolderSource.Text = dialog.SelectedPath;
			}

		}

		private void ButtonFolder_Click(object sender, RoutedEventArgs e)
		{

			string source, destination;
			source = TextBoxFolderSource.Text;
			destination = TextBoxFolderDestination.Text;

			if (string.IsNullOrEmpty(source))
				throw new Exception("Source cannot be empty.");
			if (string.IsNullOrEmpty(destination))
				throw new Exception("Destination cannot be empty.");

			try
			{
				Task.Run(() => _client.SendFolder(source, destination, false));
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		//File
		private void ButtonChooseFile_Click(object sender, RoutedEventArgs e)
		{
			OpenFileDialog dialog = new OpenFileDialog();
			dialog.InitialDirectory = "c:\\";
			var filter = "All Files | *.*";
			dialog.Filter = filter;
			dialog.FilterIndex = 1;
			dialog.RestoreDirectory = true;
			dialog.Multiselect = false;
			DialogResult result = dialog.ShowDialog();

			if (result == System.Windows.Forms.DialogResult.OK)
			{
				TextBoxFileSource.Text = dialog.FileNames[0];
			}

		}

		private void ButtonFile_Click(object sender, RoutedEventArgs e)
		{
			string source, destination;
			source = TextBoxFileSource.Text;
			destination = TextBoxFileDestination.Text;

			if (string.IsNullOrEmpty(source))
				throw new Exception("Source cannot be empty.");
			if (string.IsNullOrEmpty(destination))
				throw new Exception("Destination cannot be empty.");

			try
			{
				Task.Run(() => _client.SendFile(source, destination, false));
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}

		}

		private void Button_Click(object sender, RoutedEventArgs e)
		{
			_client.StartClient(_client.Ip, _client.Port);
		}

		private void Button1_Click(object sender, RoutedEventArgs e)
		{
			_client.Close();
		}
	}
}
