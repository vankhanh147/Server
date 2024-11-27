using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Server
{
    public partial class Form1 : Form
    {
        private TcpListener _server;
        private Thread _listenThread;
        private List<TcpClient> _clients = new List<TcpClient>();
        private bool _isRunning = false;
        private Dictionary<TcpClient, string> _clientList = new Dictionary<TcpClient, string>();
        private Dictionary<string, List<TcpClient>> _groups = new Dictionary<string, List<TcpClient>>();

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }
        private void btnStart_Click(object sender, EventArgs e)
        {
            try
            {
                int port = int.Parse(txtPort.Text);
                _server = new TcpListener(IPAddress.Parse(txtHost.Text), port);
                _server.Start();
                _isRunning = true;

                _listenThread = new Thread(ListenForClients);
                _listenThread.IsBackground = true;
                _listenThread.Start();

                AppendMessage("Server started...");
                btnStart.Enabled = false;
                btnStop.Enabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }
        private void btnStop_Click(object sender, EventArgs e)
        {
            _isRunning = false;

            foreach (var client in _clients)
            {
                client.Close();
            }

            _clients.Clear();
            _server.Stop();

            AppendMessage("Server stopped...");
            btnStart.Enabled = true;
            btnStop.Enabled = false;
        }
        private void ListenForClients()
        {
            while (_isRunning)
            {
                try
                {
                    TcpClient client = _server.AcceptTcpClient();
                    _clients.Add(client);

                    Thread clientThread = new Thread(HandleClientCommunication);
                    clientThread.IsBackground = true;
                    clientThread.Start(client);

                    AppendMessage("New client connected...");
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        AppendMessage($"Error: {ex.Message}");
                    }
                }
            }
        }
        private void HandleClientCommunication(object obj)
        {
            TcpClient client = (TcpClient)obj;
            NetworkStream stream = client.GetStream();

            byte[] buffer = new byte[1024];
            int bytesRead;

            try
            {
                // Đọc tên client khi kết nối
                bytesRead = stream.Read(buffer, 0, buffer.Length);
                string clientName = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

                lock (_clientList)
                {
                    _clientList[client] = clientName;
                    UpdateClientList();
                }

                AppendMessage($"Client connected: {clientName}");

                // Lắng nghe tin nhắn từ client
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if (message.StartsWith("PRIVATE|"))
                    {
                        HandlePrivateMessage(clientName, message);
                    }
                    else if (message.StartsWith("IMAGE|"))
                    {
                        HandleImageMessage(client, message, stream);
                    }
                    else if (message.StartsWith("FILE|"))
                    {
                        AppendMessage($"Processing FILE message: {message}");
                        HandleFileReception(client, message, stream);
                    }
                    else if (message.StartsWith("GROUP|"))
                    {
                        HandleGroupMessage(client, message);
                    }
                   else if (message.StartsWith("CREATEGROUP|"))
                    {
                        HandleCreateGroup(client, message);
                    }


                    else
                    {
                        AppendMessage($"{clientName}: {message}");
                        BroadcastMessage($"{clientName}: {message}", client);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendMessage($"A client disconnected: {ex.Message}");
            }
            finally
            {
                lock (_clientList)
                {
                    _clientList.Remove(client);
                    UpdateClientList();
                }
                client.Close();
            }
        }
        private void UpdateGroupList()
        {
            string groupListMessage = "GROUPLIST|" + string.Join(",", _groups.Keys);

            foreach (var client in _clients)
            {
                SendMessageToClient(client, groupListMessage);
            }
        }
        private void HandleCreateGroup(TcpClient senderClient, string message)
        {
            string[] parts = message.Split('|');
            if (parts.Length < 3)
            {
                AppendMessage("Malformed create group message.");
                return;
            }

            string groupName = parts[1];
            string[] members = parts[2].Split(',');

            if (_groups.ContainsKey(groupName))
            {
                AppendMessage($"Group {groupName} already exists.");
                return;
            }

            List<TcpClient> groupMembers = new List<TcpClient> { senderClient };

            foreach (var memberName in members)
            {
                var memberClient = _clientList.FirstOrDefault(c => c.Value == memberName).Key;
                if (memberClient != null && !groupMembers.Contains(memberClient))
                {
                    groupMembers.Add(memberClient);
                }
                else
                {
                    AppendMessage($"Warning: Member {memberName} not found.");
                }
            }

            _groups[groupName] = groupMembers;
            AppendMessage($"Group {groupName} created successfully with members: {string.Join(", ", members)}.");

            foreach (var member in groupMembers)
            {
                SendMessageToClient(member, $"GROUPADDED|{groupName}|{string.Join(",", groupMembers.Select(c => _clientList[c]))}");
            }
        }
        private void HandleGroupMessage(TcpClient senderClient, string message)
        {
            try
            {
                // Tin nhắn có định dạng: "GROUP|GroupName|MessageContent"
                string[] parts = message.Split('|');
                if (parts.Length < 3)
                {
                    AppendMessage("Malformed group message received.");
                    return;
                }

                string groupName = parts[1].Trim();
                string messageContent = parts[2].Trim();

                if (_groups.ContainsKey(groupName))
                {
                    string senderName = _clientList[senderClient];
                    string formattedMessage = $"GROUP FROM {groupName}|{senderName}: {messageContent}";

                    foreach (var member in _groups[groupName])
                    {
                        if (member != senderClient) // Không gửi lại cho chính người gửi
                        {
                            SendMessageToClient(member, formattedMessage);
                        }
                    }

                    AppendMessage($"Group message sent to {groupName}: {messageContent}");
                }
                else
                {
                    AppendMessage($"Group {groupName} not found.");
                }
            }
            catch (Exception ex)
            {
                AppendMessage($"Error handling group message: {ex.Message}");
            }
        }
        private void SendMessageToClient(TcpClient client, string message)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[] buffer = Encoding.UTF8.GetBytes(message + Environment.NewLine);
                stream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                AppendMessage($"Error sending message to client: {ex.Message}");
            }
        }
        private void HandleFileReception(TcpClient senderClient, string header, NetworkStream senderStream)
        {
            try
            {
                string[] parts = header.Split('|');
                if (parts.Length < 4)
                {
                    AppendMessage("Malformed file header received.");
                    return;
                }

                string targetName = parts[1]; // Có thể là tên nhóm hoặc client
                string senderName = _clientList.ContainsKey(senderClient) ? _clientList[senderClient] : "Unknown";
                string fileName = parts[2];
                int fileSize = int.Parse(parts[3]);

                // Nhận dữ liệu file từ sender
                byte[] fileBytes = new byte[fileSize];
                int totalBytesRead = 0;
                while (totalBytesRead < fileSize)
                {
                    int bytesRead = senderStream.Read(fileBytes, totalBytesRead, fileSize - totalBytesRead);
                    if (bytesRead == 0) throw new Exception("Connection closed during file transfer.");
                    totalBytesRead += bytesRead;
                }

                // Xử lý gửi file
                if (_groups.ContainsKey(targetName)) // Nếu là nhóm
                {
                    foreach (var member in _groups[targetName])
                    {
                        if (member != senderClient) // Không gửi lại cho chính người gửi
                        {
                            SendFileToClient(member, senderName, fileName, fileBytes.Length, fileBytes);
                        }
                    }
                    AppendMessage($"File '{fileName}' sent from {senderName} to group {targetName}");
                }
                else if (_clientList.ContainsValue(targetName)) // Nếu là client cá nhân
                {
                    var targetClient = _clientList.FirstOrDefault(c => c.Value == targetName).Key;
                    if (targetClient != null)
                    {
                        SendFileToClient(targetClient, senderName, fileName, fileBytes.Length, fileBytes);
                        AppendMessage($"File sent from {senderName} to {targetName}: {fileName}");
                    }
                    else
                    {
                        AppendMessage($"Error: Client '{targetName}' not found.");
                    }
                }
                else
                {
                    AppendMessage($"Error: Group or client '{targetName}' not found.");
                }
            }
            catch (Exception ex)
            {
                AppendMessage($"Error handling file: {ex.Message}");
            }
        }
        private void HandleGroupFileReception(TcpClient senderClient, string header, NetworkStream senderStream)
        {
            try
            {
                string[] parts = header.Split('|');
                if (parts.Length < 5)
                {
                    AppendMessage("Malformed group file header received.");
                    return;
                }

                string groupName = parts[1];
                string senderName = _clientList[senderClient];
                string fileName = parts[2];
                int fileSize = int.Parse(parts[3]);

                // Nhận file từ client gửi
                byte[] fileBytes = new byte[fileSize];
                int totalBytesRead = 0;
                while (totalBytesRead < fileSize)
                {
                    int bytesRead = senderStream.Read(fileBytes, totalBytesRead, fileSize - totalBytesRead);
                    if (bytesRead == 0) throw new Exception("Connection closed during file reception.");
                    totalBytesRead += bytesRead;
                }

                // Gửi file tới tất cả thành viên trong nhóm, trừ người gửi
                if (_groups.ContainsKey(groupName))
                {
                    foreach (var member in _groups[groupName])
                    {
                        if (member != senderClient)
                        {
                            SendGroupFileToClient(member, groupName, senderName, fileName, fileBytes);
                        }
                    }
                    AppendMessage($"File '{fileName}' sent from {senderName} to group {groupName}");
                }
                else
                {
                    AppendMessage($"Group {groupName} not found.");
                }
            }
            catch (Exception ex)
            {
                AppendMessage($"Error handling group file: {ex.Message}");
            }
        }
        private void SendGroupFileToClient(TcpClient client, string groupName, string sender, string fileName, byte[] fileBytes)
        {
            try
            {
                NetworkStream stream = client.GetStream();

                // Gửi header file
                string header = $"GROUP_FILE|{groupName}|{sender}|{fileName}|{fileBytes.Length}";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header + Environment.NewLine);
                stream.Write(headerBytes, 0, headerBytes.Length);

                // Gửi dữ liệu file
                stream.Write(fileBytes, 0, fileBytes.Length);
            }
            catch (Exception ex)
            {
                AppendMessage($"Error sending group file to client: {ex.Message}");
            }
        }
        private void SendFileToClient(TcpClient targetClient, string senderClientName, string fileName, int fileSize, byte[] fileBytes)
        {
            try
            {
                NetworkStream targetStream = targetClient.GetStream();

                // Gửi header file
                string targetHeader = $"FILE|{senderClientName}|{fileName}|{fileSize}{Environment.NewLine}";
                byte[] targetHeaderBytes = Encoding.UTF8.GetBytes(targetHeader);
                targetStream.Write(targetHeaderBytes, 0, targetHeaderBytes.Length);

                // Gửi dữ liệu file
                int totalBytesSent = 0;
                while (totalBytesSent < fileBytes.Length)
                {
                    int chunkSize = Math.Min(1024, fileBytes.Length - totalBytesSent); // Gửi 1024 byte mỗi lần
                    targetStream.Write(fileBytes, totalBytesSent, chunkSize);
                    totalBytesSent += chunkSize;
                }

                AppendMessage($"File '{fileName}' sent successfully to client.");
            }
            catch (Exception ex)
            {
                AppendMessage($"Error sending file to client: {ex.Message}");
            }
        }
        private void HandleImageMessage(TcpClient senderClient, string header, NetworkStream senderStream)
        {
            try
            {
                string[] parts = header.Split('|');
                if (parts.Length < 4)
                {
                    AppendMessage("Malformed image header received.");
                    return;
                }

                string targetName = parts[1];
                string senderName = _clientList[senderClient];
                string fileName = parts[2];
                int fileSize = int.Parse(parts[3]);

                // Nhận dữ liệu ảnh
                byte[] imageBytes = new byte[fileSize];
                int totalBytesRead = 0;
                while (totalBytesRead < fileSize)
                {
                    int bytesRead = senderStream.Read(imageBytes, totalBytesRead, fileSize - totalBytesRead);
                    if (bytesRead == 0) throw new Exception("Connection closed during image transfer.");
                    totalBytesRead += bytesRead;
                }

                // Gửi ảnh đến client cá nhân
                var targetClient = _clientList.FirstOrDefault(c => c.Value == targetName).Key;
                if (targetClient != null)
                {
                    SendImageToClient(targetClient, targetName, senderName, fileName, fileSize, imageBytes);
                    AppendMessage($"Image sent from {senderName} to {targetName}: {fileName}");
                }
                else
                {
                    AppendMessage($"Error: Client '{targetName}' not found.");
                }
            }
            catch (Exception ex)
            {
                AppendMessage($"Error handling image: {ex.Message}");
            }
        }
        private void SendImageToClient(TcpClient targetClient, string targetName, string senderName, string fileName, int fileSize, byte[] imageBytes)
        {
            try
            {
                NetworkStream targetStream = targetClient.GetStream();

                // Gửi header ảnh
                string header = $"IMAGE|{senderName}|{fileName}|{fileSize}{Environment.NewLine}";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                targetStream.Write(headerBytes, 0, headerBytes.Length);

                // Gửi dữ liệu ảnh
                targetStream.Write(imageBytes, 0, imageBytes.Length);

                AppendMessage($"Image '{fileName}' sent successfully to {targetName}.");
            }
            catch (Exception ex)
            {
                AppendMessage($"Error sending image to {targetName}: {ex.Message}");
            }
        }
        private void HandleGroupImageReception(TcpClient senderClient, string header, NetworkStream senderStream)
        {
            try
            {
                string[] parts = header.Split('|');
                if (parts.Length < 5)
                {
                    AppendMessage("Malformed group image header received.");
                    return;
                }

                string groupName = parts[1];
                string senderName = _clientList[senderClient];
                string fileName = parts[2];
                int fileSize = int.Parse(parts[3]);

                // Nhận dữ liệu ảnh
                byte[] imageBytes = new byte[fileSize];
                int totalBytesRead = 0;
                while (totalBytesRead < fileSize)
                {
                    int bytesRead = senderStream.Read(imageBytes, totalBytesRead, fileSize - totalBytesRead);
                    if (bytesRead == 0) throw new Exception("Connection closed during image reception.");
                    totalBytesRead += bytesRead;
                }

                // Gửi ảnh tới tất cả thành viên nhóm
                if (_groups.ContainsKey(groupName))
                {
                    foreach (var member in _groups[groupName])
                    {
                        if (member != senderClient) // Không gửi lại cho chính người gửi
                        {
                            SendImageToClient(member, groupName, senderName, fileName, fileSize, imageBytes);
                        }
                    }
                    AppendMessage($"Image '{fileName}' sent from {senderName} to group {groupName}");
                }
                else
                {
                    AppendMessage($"Group {groupName} not found.");
                }
            }
            catch (Exception ex)
            {
                AppendMessage($"Error handling group image: {ex.Message}");
            }
        }
        private void HandlePrivateMessage(string senderClient, string message)
        {
            string[] parts = message.Split('|');
            if (parts.Length < 3)
            {
                AppendMessage($"Invalid private message format from {senderClient}.");
                return;
            }

            string targetClientName = parts[1];
            string privateMessage = parts[2];

            TcpClient targetClient;
            lock (_clientList)
            {
                targetClient = _clientList.FirstOrDefault(c => c.Value == targetClientName).Key;
            }

            if (targetClient != null)
            {
                try
                {
                    string formattedMessage = $"PRIVATE FROM {senderClient}: {privateMessage}{Environment.NewLine}";
                    byte[] buffer = Encoding.UTF8.GetBytes(formattedMessage);
                    targetClient.GetStream().Write(buffer, 0, buffer.Length);

                    AppendMessage($"Private message sent from {senderClient} to {targetClientName}: {privateMessage}");
                }
                catch (Exception ex)
                {
                    AppendMessage($"Failed to send private message to {targetClientName}: {ex.Message}");
                }
            }
            else
            {
                AppendMessage($"Target client {targetClientName} not found.");
            }
        }

        private void HandleImageTransfer(TcpClient senderClient, string header, byte[] imageData)
        {
            string[] parts = header.Split('|');
            if (parts.Length < 4)
            {
                AppendMessage("Malformed image header received.");
                return;
            }

            string targetClientName = parts[1];
            string fileName = parts[2];
            int fileSize = int.Parse(parts[3]);

            TcpClient targetClient;
            lock (_clientList)
            {
                targetClient = _clientList.FirstOrDefault(c => c.Value == targetClientName).Key;
            }

            if (targetClient != null)
            {
                try
                {
                    NetworkStream targetStream = targetClient.GetStream();

                    // Gửi header
                    string imageHeader = $"IMAGE|{_clientList[senderClient]}|{fileName}|{fileSize}{Environment.NewLine}";
                    byte[] headerBytes = Encoding.UTF8.GetBytes(imageHeader);
                    targetStream.Write(headerBytes, 0, headerBytes.Length);

                    // Gửi dữ liệu ảnh
                    targetStream.Write(imageData, 0, imageData.Length);

                    AppendMessage($"Image sent from {_clientList[senderClient]} to {targetClientName}: {fileName}");
                }
                catch (Exception ex)
                {
                    AppendMessage($"Failed to send image to {targetClientName}: {ex.Message}");
                }
            }
            else
            {
                AppendMessage($"Target client {targetClientName} not found for image transfer.");
            }
        }


        private void UpdateClientList()
        {
            string clientListMessage = "CLIENTLIST|" + string.Join(",", _clientList.Values);

            foreach (var client in _clients)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] buffer = Encoding.UTF8.GetBytes(clientListMessage + Environment.NewLine);
                    stream.Write(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    AppendMessage($"Error updating client list for a client: {ex.Message}");
                }
            }
        }


        private void BroadcastMessage(string message, TcpClient sender)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(message);

            foreach (var client in _clients)
            {
                if (client != sender)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        stream.Write(buffer, 0, buffer.Length);
                    }
                    catch (Exception)
                    {
                        // Ignore errors when broadcasting
                    }
                }
            }
        }

        private void AppendMessage(string message)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action(() => txtLog.AppendText(message + Environment.NewLine)));
            }
            else
            {
                txtLog.AppendText(message + Environment.NewLine);
            }
        }
        

    }
}
