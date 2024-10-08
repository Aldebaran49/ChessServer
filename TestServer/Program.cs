// See https://aka.ms/new-console-template for more information
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChessLibrary;
using Confluent.Kafka;
using Newtonsoft.Json;
using MySqlConnector;
using System.Threading.Channels;

ServerObject server = new ServerObject();// создаем сервер
await server.ListenAsync(); // запускаем сервер

class ServerObject
{
    TcpListener tcpListener = new TcpListener(IPAddress.Any, 8888); // сервер для прослушивания
    List<ClientObject> clients = new List<ClientObject>(); // все подключения
    Queue<string> playersInQueueIDs = new Queue<string>(); //ID игроков в очереди
    public List<gameRoom> rooms = new List<gameRoom>(); // Все игровые лобби
    public DBConnector Database = new DBConnector("localhost","admin","admin");


    protected internal void RemoveConnection(string id)
    {
        // получаем по id закрытое подключение
        ClientObject? client = clients.FirstOrDefault(c => c.Id == id);
        // и удаляем его из списка подключений
        if (client != null) clients.Remove(client);
        client?.Close();
    }
    // прослушивание входящих подключений
    protected internal async Task ListenAsync()
    {
        try
        {
            tcpListener.Start();
            Console.WriteLine("Сервер запущен. Ожидание подключений...");

            while (true)
            {
                TcpClient tcpClient = await tcpListener.AcceptTcpClientAsync(); //Ловим клиента, который хочет подключиться

                ClientObject clientObject = new ClientObject(tcpClient, this); // Создаём "переходник" для клиента
                clients.Add(clientObject); // Добавляем его в список
                Task.Run(clientObject.ProcessAsync); // Взаимодействие с клиентом выносится в отдельный поток
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        finally
        {
            Disconnect();
        }
    }

    // трансляция сообщения подключенным клиентам
    /*protected internal async Task BroadcastMessageAsync(string message, string id)
    {
        foreach (var client in clients)
        {
            if (client.Id != id) // если id клиента не равно id отправителя
            {
                await client.Writer.WriteLineAsync(message); //передача данных
                await client.Writer.FlushAsync();
            }
        }
    }*/
    protected internal void AddPlayerInQueue(string id)
    {
        playersInQueueIDs.Enqueue(id);
        Console.WriteLine(id + " добавлен в очередь. В очереди сейчас: " + playersInQueueIDs.Count +" пользователей");
        if (playersInQueueIDs.Count >= 2)
        {
            Console.WriteLine("В очереди достаточно игроков! Начинаем игру");
            //sendConfRequests(playersInQueueIDs.Dequeue(), playersInQueueIDs.Dequeue());
            startGame(playersInQueueIDs.Dequeue(),playersInQueueIDs.Dequeue());
            
        }
    }
    protected internal void RemovePlayerFromQueue(string id)
    {
        Queue<string> temp2 = new Queue<string>();
        while (playersInQueueIDs.Count > 0)
        {
            string temp = playersInQueueIDs.Dequeue();
            if (temp != id)
                temp2.Enqueue(temp);
        }
        playersInQueueIDs.Clear();
        playersInQueueIDs = temp2;
    }
    /*private void sendConfRequests(string id1, string id2)
    {
        ClientObject player1 = Find(id1);
        ClientObject player2 = Find(id2);
        player1.sendMessage("conf");
        player2.sendMessage("conf");
    }*/
    private void startGame (string id1, string id2)
    {
        ClientObject player1 = Find(id1);
        player1.AssingColor(1);
        _ = player1.sendMessage("White");
        ClientObject player2 = Find(id2);
        player2.AssingColor(0);
        _ = player2.sendMessage("Black");
        gameRoom room = new gameRoom(player1, player2);
        player1.PrepareForGame(room); 
        player2.PrepareForGame(room);
        Console.WriteLine("Отправлено уведомление о начале игры игрокам " + id1 + " и " + id2);
        return; //СТАРТ ИГРЫ
    }
    private ClientObject Find (string id)
    {
        foreach (var client in clients)
        {
            if (client.Id == id) 
            { 
                return client;
            }
        }
        return clients[0];
    }
    // отключение всех клиентов
    protected internal void Disconnect()
    {
        foreach (var client in clients)
        {
            client.Close(); //отключение клиента
        }
        tcpListener.Stop(); //остановка сервера
    }
}
class ClientObject
{
    protected internal string Id { get; } = Guid.NewGuid().ToString();
    protected internal StreamWriter Writer { get; } //Поток записи
    protected internal StreamReader Reader { get; } //Поток чтения

    TcpClient client;
    ServerObject server; // объект сервера
    string userName;
    bool inQueue = false;
    public bool inGame = false;
    public gameRoom currentGameRoom;
    public Player color;
    public Player AssingColor(int x)
    {
        if(x==1)
        {
            color= Player.White;
        }
        else
        {
            color = Player.Black;
        }
        return color;
    }

    public ClientObject(TcpClient tcpClient, ServerObject serverObject)
    {
        client = tcpClient;
        server = serverObject;
        
        // получаем NetworkStream для взаимодействия с сервером
        var stream = client.GetStream();
        // создаем StreamReader для чтения данных
        Reader = new StreamReader(stream);
        // создаем StreamWriter для отправки данных
        Writer = new StreamWriter(stream);
        userName = "anonymous";
    }

    public async Task ProcessAsync() // Взаимодействие с клиентом
    {
        try
        {
            string? message;
            /*получаем имя пользователя
            string? userName = await Reader.ReadLineAsync();
            string? message = $"{userName} вошел в чат";
            await server.BroadcastMessageAsync(message, Id);*/
            Console.WriteLine($"{userName} вошел на сервер");
            while (true)
            {
                try
                {
                    message = await Reader.ReadLineAsync();
                    if (message == null) continue;
                    if (message.Substring(0, 2) == "!!")
                    {
                        if (AuthProcessor(message))
                            await sendMessage("authcorrect");
                        else
                            await sendMessage("autherror");
                    }
                    if (message.Substring(0, 2) == "!?")
                    {
                        if (RegisterProcessor(message))
                            await sendMessage("authcorrect");
                        else
                            await sendMessage("autherror");
                    }
                    if (message == "qe")
                    {
                        Console.WriteLine("Запрос на вступление в очередь от " + userName); //ЗДЕСЬ ДЕЛАТЬ
                        if (inQueue)
                        {
                            Console.WriteLine("Запрос отклонён, игрок уже в очереди");
                        }
                        else
                        {
                            inQueue = true;
                            await sendMessage("qeok");
                            server.AddPlayerInQueue(Id);
                        }
                    }
                    if(message.Substring(0,1)=="?")
                    {
                        message = message.Substring(1);
                        Console.WriteLine(message+ "получен ответ на сервере");
                        currentGameRoom.Move(message, this);
                        
                    }
                    if(message.Substring(0,1)=="A")
                    {
                        Console.WriteLine("Другой");
                        currentGameRoom.SendToAnotherPlayer(message, this);
                    }
                    if(message == "terminated" && currentGameRoom != null)
                    {
                        currentGameRoom.Terminate();
                        server.rooms.Remove(currentGameRoom);
                    }
                    if (message == "terminated1" && currentGameRoom != null)
                    {
                        //currentGameRoom.Terminate();
                        this.sendMessage("term");
                        
                        this.inGame = false;
                        
                        this.currentGameRoom = null;
                        try
                        {
                            server.rooms.Remove(currentGameRoom);
                        }
                        catch
                        {

                        }
                    }
                    if (message.Substring(0,1)=="F")
                    {
                        message = message.Substring(1);
                        currentGameRoom.HandlePromotion(this,message);
                        
                    }
                    
                    message = $"{userName}: {message}";
                    Console.WriteLine(message);
                    
                    //await server.BroadcastMessageAsync(message, Id);
                }
                catch // Дисконнект
                {
                    message = $"{userName} покинул сервер";
                    Console.WriteLine(message);
                    //await server.BroadcastMessageAsync(message, Id);
                    if (inQueue)
                    {
                        inQueue = false;
                        server.RemovePlayerFromQueue(Id);
                    }
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
        finally
        {
            // в случае выхода из цикла закрываем ресурсы
            Console.WriteLine("Клиент "+Id + " с именем " + userName + " отключился");
            if (inQueue)
            {
                inQueue = false;
                server.RemovePlayerFromQueue(Id);
            }
            if (inGame)
            {
                currentGameRoom.Terminate();
                inGame = false;
            }
            server.RemoveConnection(Id);

        }
    }

    protected bool AuthProcessor(string msg)
    {
        string[] authData = msg.Substring(2).Split("!");
        string login = authData[0];
        string password = authData[1];
        Console.WriteLine("АВТОРИЗАЦИЯ Логин " + login + " Пароль " + password);
        if (server.Database.CheckPlayerData(login, password))
        {
            userName = login;
            return true;
        }
        return false;
    }

    protected bool RegisterProcessor(string msg)
    {
        string[] authData = msg.Substring(2).Split("!");
        string login = authData[0];
        string password = authData[1];
        Console.WriteLine("РЕГИСТРАЦИЯ Логин " + login + " Пароль " + password);
        if (server.Database.AddNewPlayer(login, password))
        {
            userName = login;
            return true;
        }
        return false;
    }

    public async void PrepareForGame(gameRoom room)
    {
        inQueue = false;
        inGame = true;
        currentGameRoom = room;
        //Thread.Sleep(100);
        await sendMessage("gameStart");
        Console.WriteLine("Отправлен gameStart");
    }
    protected internal void Close()
    {
        Writer.Close();
        Reader.Close();
        client.Close();
    }
    public async Task sendMessage(string msg)
    {
        await Writer.WriteLineAsync(msg); //передача данных
        await Writer.FlushAsync();
    }
}

class gameRoom
{
    public static ClientObject Player1;
    public static ClientObject Player2;
    public Position from1;
    public Position to1;
    GameState gameState;
    public gameRoom (ClientObject player1, ClientObject player2)
    {
        Player1 = player1;
        Player2 = player2;
        gameState = new GameState(player1.color, Board.Initial());
    }
    
   

    private readonly Dictionary<Position, Move> moveCache = new Dictionary<Position, Move>();
    private static  Position selectedPos = null;
    
    public  void CacheMoves(IEnumerable<Move> moves, ClientObject player)
    {
        Console.WriteLine("ура словарь доступных ходов");
        moveCache.Clear();
        foreach (Move move in moves)
        {
            moveCache[move.ToPos] = move;
            //Console.WriteLine($"Ключ: {move.ToPos}, Значение: {move}");
        }
        Dictoarray(player);
        
    }
    public void Dictoarray(ClientObject player)
    {
        PieceMoves[] positions = new PieceMoves[moveCache.Count];
        int index = 0;
        foreach(Position to in moveCache.Keys)
        {
            positions[index]= new PieceMoves(to.Row,to.Column);
            //Console.WriteLine("y: "+positions[index].Column+", x: " + positions[index].Row);
            index++;
        }
        string json = JsonConvert.SerializeObject(positions);
        //json = "???" + json;
        
        player.sendMessage(json);Console.WriteLine("отправил координаты доступных ходов");
    }
    public  void Move(string msg, ClientObject player)
    {
        Position pos = ToSquarePosition(msg);

        if(selectedPos == null)
        {
            Console.WriteLine("считаю доступные ходы");
           
            OnFromPositionSelected(pos,player);
        }
        else
        {
            Console.WriteLine("считаю ход");
            OnToPositionSelected(pos,player);

        }
    }
    
    private  void OnFromPositionSelected(Position pos,ClientObject player)
    {
        IEnumerable<Move> moves = gameState.LegalMovesForPiece(pos);
        if(moves.Any())
        {
            Console.WriteLine("ходы есть");
            selectedPos = pos;
            CacheMoves(moves, player);
        }
    }
    private void OnToPositionSelected(Position pos,ClientObject player)
    {
        selectedPos = null;
        player.sendMessage("hide");
        Console.WriteLine("спрятал доступные ходы на доске");
        if(moveCache.TryGetValue(pos, out Move move))
        {
            if(move.Type == MoveType.PawnPromotion)
            {
                
                Console.WriteLine("выбираю фигуру");
                string coord;
                coord = "prom"+move.ToPos.Row.ToString()+ move.ToPos.Column.ToString()+move.FromPos.Row.ToString()+ move.FromPos.Column.ToString();
                from1 = move.FromPos;
                to1 = move.ToPos;
                player.sendMessage(coord);
            }
            else
            {
                HandleMove(move);
                Console.WriteLine("вроде должен ходить");
                AskServertoClient(player);
            }
            
            
        }
        
        
    }
    public void HandlePromotion(ClientObject player, string msg)
    {

        PieceType PieceSelected;
        if (msg == "Rook")
        {
            Console.WriteLine("Ладья");
            PieceSelected = PieceType.Rook;
        }
        else if (msg == "Bishop")
        {
            Console.WriteLine("Слон");
            PieceSelected = PieceType.Bishop;
        }
        else if (msg == "Knight")
        {
            Console.WriteLine("Конь");
            PieceSelected = PieceType.Knight;
        }
        else
        {
            Console.WriteLine("Королева");
            PieceSelected = PieceType.Queen;
        }
        Move promMove = new PawnPromotion(from1, to1, PieceSelected);
        HandleMove(promMove);
        AskServerToClientPromotion(msg,player);
    }
    private void AskServerToClientPromotion(string msg,ClientObject player)
    {
        msg = "pp" + msg;
        player.sendMessage(msg);
        if (gameState.IsGameOver())
        {
            
            if (player == Player1)
            {
                Player1.sendMessage("win!");
                Player2.sendMessage("Lose!");
            }
            else
            {
                Player2.sendMessage("win!");
                Player1.sendMessage("Lose!");
            }
            //Terminate();
        }
    }
    
    private void AskServertoClient(ClientObject player)
    {
        string yes = "да";
        player.sendMessage(yes); Console.WriteLine("отправляю ход клиенту");
        if (gameState.IsGameOver())
        {
            
            if(player == Player1)
            {
                Player1.sendMessage("win!");
                Player2.sendMessage("Lose!");
            }
            else
            {
                Player2.sendMessage("win!");
                Player1.sendMessage("Lose!");
            }
            //Terminate();
        }
    }

    public async void SendToAnotherPlayer(string msg, ClientObject player)
    {
        if (Player1 == player)
        {
            await Player2.sendMessage(msg);
        }
        else if (Player2 == player)
        {
            await Player1.sendMessage(msg);
        }
    }
    private void HandleMove(Move move)
    {
        gameState.MakeMove(move);
    }
    public Position ToSquarePosition(string msg)
    {
        string[] parts = msg.Trim('(', ')').Split(',');
        if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int column) &&
            int.TryParse(parts[0].Trim(), out int row))
        {
            return new Position(row, column);
        }
        else
        {
            Console.WriteLine("все плохо");
            return new Position(0, 0);
        }
    }
    public async void Terminate()
    {
        Player1.sendMessage("term");
        Player2.sendMessage("term");
        Player1.inGame = false;
        Player2.inGame = false;
        Player1.currentGameRoom = null;
        Player2.currentGameRoom = null;
    }
}
public struct PieceMoves
{
    public int y, x;
    public PieceMoves(int y, int x) { this.y = y; this.x = x; }
}

class DBConnector
{
    private string serverName;
    private string username;
    private string serverPassword;
    public DBConnector(string serverName, string username, string serverPassword)
    {
        this.serverName = serverName;
        this.username = username;
        this.serverPassword = serverPassword;
    }
    public void connect()
    {

    }
    public bool CheckPlayerData(string login, string password)
    {
        try
        {
            string conn = "Server=" + serverName + ";User ID=" + username + ";Password=" + serverPassword + ";Database=chess_players";
            using var connection = new MySqlConnection(conn);
            connection.Open();
            using var command = new MySqlCommand("SELECT password FROM players WHERE login='" + login + "';", connection);
            using var reader = command.ExecuteReader();
            
            reader.Read();
            string temp = reader.GetValue(0).ToString();
            Console.WriteLine(temp);
            if (temp == password)
                return true;
            else
                return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine("DATABASE ERROR: " + ex.Message);
            return false;
        }
    }
    public bool AddNewPlayer(string login, string password)
    {
        try
        {
            string conn = "Server=" + serverName + ";User ID=" + username + ";Password=" + serverPassword + ";Database=chess_players";
            using var connection = new MySqlConnection(conn);
            connection.Open();
            using var command = new MySqlCommand("SELECT login FROM players WHERE login='" + login + "';", connection);
            using var reader = command.ExecuteReader();
            string temp;
            Console.WriteLine("Debug1");
            reader.Read();
            try
            {
                temp = reader.GetValue(0).ToString();
            }
            catch (Exception ex)
            {
                temp = null;
            }
            connection.Close();
            Console.WriteLine("Debug2");
            if (temp == null)
            {
                using var connection1 = new MySqlConnection(conn);
                connection1.Open();
                using var command1 = new MySqlCommand("INSERT INTO players (login, password) VALUES ('"+ login +"', '"+ password +"');", connection1);
                command1.ExecuteNonQuery();
                Console.WriteLine("Debug3");
                connection1.Close();
                return true;
            }
            else
            {
                Console.WriteLine("Такой пользователь уже есть");
                return false;
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine("DATABASE ERROR: " + ex.Message);
            return false;
        }
    }
}