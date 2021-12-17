using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Cowboy.Sockets;
using MahjongAI.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MahjongAI
{
    internal class MajsoulExtension : PlatformClient
    {
        private readonly int port = 14782;
        private readonly char delimiter = '\x00';
        private AsyncTcpSocketServer server;
        private AsyncTcpSocketSession client;
        private String buffer = "";
        private IEnumerable<JToken> operationList;
        private bool nextReach = false;
        private bool gameEnded = false;
        private Tile lastDiscardedTile;
        private int accountId = 0;
        private int playerSeat = 0;
        private bool continued = false;
        private bool syncing = false;
        private Queue<Message> pendingActions = new Queue<Message>();
        private bool inPrivateRoom = false;
        private bool continuedBetweenGames = false;
        private bool gameStarted = false;
        private Stopwatch stopwatch = new Stopwatch();
        private Random random = new Random();
        private Dictionary<string, Timer> timers = new Dictionary<string, Timer>();


        public MajsoulExtension(Config config) : base(config)
        {
            server = new AsyncTcpSocketServer(port, OnSessionDataReceived, OnSessionStarted, OnSessionClosed);
            server.Listen();
            Console.WriteLine(string.Format("Listening on: {0}.", server.ListenedEndPoint));
        }

        public async Task OnSessionStarted(AsyncTcpSocketSession session)
        {
            client = session;
            server.SendToAsync(client, Encoding.UTF8.GetBytes("ACK")).Wait();
            Console.WriteLine(string.Format("TCP session {0} has connected.", session.RemoteEndPoint));
            await Task.CompletedTask;
        }

        public async Task OnSessionClosed(AsyncTcpSocketSession session)
        {
            Console.WriteLine(string.Format("TCP session {0} has disconnected.", session));
            await Task.CompletedTask;
        }

        public async Task OnSessionDataReceived(AsyncTcpSocketSession session, byte[] data, int offset, int count)
        {
            var temp = Encoding.UTF8.GetString(data, offset, count);
            buffer += temp;
            for (int mark = buffer.IndexOf(delimiter); mark != -1; mark = buffer.IndexOf(delimiter))
            {
                var scope = buffer.Substring(0, mark);
                buffer = buffer.Substring(mark + 1);
                //Console.WriteLine(string.Format("Message: {0}", scope));
                Message message = JsonConvert.DeserializeObject<Message>(scope);
                await Task.Run(() => HandleMessage(message));
            }
        }

        private void Deley(int minimumTimespanSeconds = 2)
        {
            if (stopwatch.Elapsed < TimeSpan.FromSeconds(minimumTimespanSeconds))
            {
                Thread.Sleep(random.Next(2, 4) * 1000);
            }
        }

        private async Task Send(object data)
        {
            var json = JsonConvert.SerializeObject(data) + '\x00';
            try
            {
                await server.SendToAsync(client, Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
        }

        private void DelayedNextReady()
        {
            new Thread(() =>
            {
                Thread.Sleep(5000);
                if (!gameEnded)
                {
                    NextReady();
                }
            }).Start();
        }

        private int NormalizedPlayerId(int seat)
        {
            return (seat - playerSeat + gameData.players.Count()) % gameData.players.Count();
        }

        private void HandleMessage(Message message, bool forSync = false)
        {
            if (message == null)
            {
                return;
            }

            if (syncing && !forSync && message.method != ".lq.FastTest.fetchGamePlayerState")
            {
                pendingActions.Enqueue(message);
                return;
            }

            if (message.method != null && timers.ContainsKey(message.method))
            {
                timers[message.method].Dispose();
            }

            if (message.method == ".lq.FastTest.checkNetworkDelay")
            {
                return;
            }

            if (message.type == MessageType.Req && message.method == ".lq.FastTest.authGame")
            {
                accountId = (int)message.data["accountId"];
            }
            else if (message.type == MessageType.Res && message.method == ".lq.FastTest.authGame")
            {
                gameStarted = true;
                InvokeOnGameStart(continued);
                // TODO: syncGame requires outer controller support
            }

            if (message.type == MessageType.Req)
            {
                return;
            }

            if (message.method == ".lq.NotifyPlayerLoadGameReady")
            {
                playerSeat = message.data["readyIdList"].Select(t => (int)t).ToList().IndexOf(accountId);
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionMJStart")
            {
                gameEnded = false;
            }
            else if (message.method == ".lq.NotifyGameEndResult")
            {
                gameEnded = true;
                Bye();
                InvokeOnGameEnd();
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionHule")
            {
                var data = message.data["data"];
                int[] points = data["scores"].Select(t => (int)t).ToArray();
                int[] rawPointDeltas = data["deltaScores"].Select(t => (int)t).ToArray();
                int[] pointDeltas = new int[gameData.players.Count()];

                for (var i = 0; i < gameData.players.Count(); i++)
                {
                    gameData.players[NormalizedPlayerId(i)].point = points[i];
                    pointDeltas[NormalizedPlayerId(i)] = rawPointDeltas[i];
                }

                foreach (var agari in data["hules"])
                {
                    int seat = 0;
                    bool zimo = false, qinjia = false, yiman = false;
                    int pointRong = 0, pointZimoXian = 0, pointZimoQin = 0;
                    if (agari["seat"] != null) seat = (int)agari["seat"];
                    if (agari["zimo"] != null) zimo = (bool)agari["zimo"];
                    if (agari["yiman"] != null) yiman = (bool)agari["yiman"];
                    if (agari["qinjia"] != null) qinjia = (bool)agari["qinjia"];
                    if (agari["pointRong"] != null) pointRong = (int)agari["pointRong"];
                    if (agari["pointZimoQin"] != null) pointZimoQin = (int)agari["pointZimoQin"];
                    if (agari["pointZimoXian"] != null) pointZimoXian = (int)agari["pointZimoXian"];
                    Player who = gameData.players[NormalizedPlayerId(seat)];
                    Player fromWho = pointDeltas.Count(s => s < 0) == 1 ? gameData.players[Array.FindIndex(pointDeltas, s => s < 0)] : who;
                    int point = !zimo ? pointRong : qinjia ? pointZimoXian * 3 : pointZimoXian * 2 + pointZimoQin;
                    if (gameData.lastTile != null)
                    {
                        gameData.lastTile.IsTakenAway = true;
                    }
                    if (yiman)
                    {
                        Console.WriteLine("Yakuman");
                    }
                    InvokeOnAgari(who, fromWho, point, pointDeltas, gameData.players);
                }

                DelayedNextReady();
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionLiuJu")
            {
                InvokeOnAgari(null, null, 0, new[] { 0, 0, 0, 0 }, gameData.players);
                DelayedNextReady();
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionNoTile")
            {
                var scoreObj = message.data["data"]["scores"][0];
                int[] rawPointDeltas = scoreObj["deltaScores"] != null ? scoreObj["deltaScores"].Select(t => (int)t).ToArray() : new[] { 0, 0, 0, 0 };
                int[] pointDeltas = new int[gameData.players.Count()];
                for (var i = 0; i < gameData.players.Count(); i++)
                {
                    gameData.players[NormalizedPlayerId(i)].point = (int)scoreObj["oldScores"][i] + rawPointDeltas[i];
                    pointDeltas[NormalizedPlayerId(i)] = rawPointDeltas[i];
                }
                InvokeOnAgari(null, null, 0, pointDeltas, gameData.players);
                DelayedNextReady();
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionNewRound")
            {
                Tile.Reset();
                gameData = new GameData();
                HandleInit(message.data["data"]);

                if (!syncing)
                {
                    InvokeOnInit(/* continued */ false, gameData.direction, gameData.seq, gameData.seq2, gameData.players);
                }

                if (player.hand.Count > 13)
                {
                    operationList = message.data["data"]["operation"]["operationList"];
                    if (!syncing)
                    {
                        Thread.Sleep(2000); // 等待发牌动画结束
                        stopwatch.Restart();
                        InvokeOnDraw(player.hand.Last());
                    }
                }
            }
            else if (message.method == ".lq.FastTest.syncGame")
            {
                syncing = true;
                // TODO: syncGame requires outer controller support
            }
            else if (message.method == ".lq.FastTest.fetchGamePlayerState")
            {
                bool inited = false;
                //playerSeat = message.data["stateList"].ToList().IndexOf("AUTH") - 2;

                while (pendingActions.Count > 1)
                {
                    var actionMessage = pendingActions.Dequeue();
                    if (actionMessage.method == ".lq.ActionPrototype" && (string)actionMessage.data["name"] == "ActionNewRound")
                    {
                        inited = true;
                    }
                    HandleMessage(actionMessage, forSync: true);
                }

                //Send(wsGameNode, ".lq.FastTest.finishSyncGame", new { }).Wait();
                syncing = false;

                if (inited)
                {
                    InvokeOnInit(/* continued */ true, gameData.direction, gameData.seq, gameData.seq2, gameData.players);
                }

                // Queue里的最后一个action需要响应
                if (pendingActions.Count > 0)
                {
                    HandleMessage(pendingActions.Dequeue());
                }

                if (continuedBetweenGames)
                {
                    NextReady();
                }
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionDealTile")
            {
                var data = message.data["data"];
                var seat = 0;
                var remainingTile = 0;
                if (data["leftTileCount"] != null) remainingTile = (int)data["leftTileCount"];
                gameData.remainingTile = remainingTile;
                if (data["doras"] != null)
                {
                    var doras = data["doras"].Select(t => (string)t);
                    foreach (var dora in doras.Skip(gameData.dora.Count))
                    {
                        gameData.dora.Add(new Tile(dora));
                    }
                }
                if (data["seat"] != null) seat = (int)data["seat"];
                if (NormalizedPlayerId(seat) == 0)
                {
                    Tile tile = new Tile((string)data["tile"]);
                    player.hand.Add(tile);
                    gameData.lastTile = tile;
                    operationList = data["operation"]["operationList"];
                    if (!syncing)
                    {
                        stopwatch.Restart();
                        InvokeOnDraw(tile);
                    }
                }
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionDiscardTile")
            {
                var data = message.data["data"];
                var seat = 0;
                if (data["seat"] != null) seat = (int)data["seat"];
                Player currentPlayer = gameData.players[NormalizedPlayerId(seat)];
                if (data["moqie"] != null && !(bool)data["moqie"])
                {
                    currentPlayer.safeTiles.Clear();
                }
                var tileName = (string)data["tile"];
                if (currentPlayer == player)
                {
                    if (lastDiscardedTile == null || lastDiscardedTile.OfficialName != tileName)
                    {
                        lastDiscardedTile = player.hand.First(t => t.OfficialName == tileName);
                    }
                    player.hand.Remove(lastDiscardedTile);
                }
                Tile tile = currentPlayer == player ? lastDiscardedTile : new Tile(tileName);
                lastDiscardedTile = null;
                currentPlayer.graveyard.Add(tile);
                gameData.lastTile = tile;
                foreach (var p in gameData.players)
                {
                    p.safeTiles.Add(tile);
                }
                if ((data["isLiqi"] != null && (bool)data["isLiqi"]) || (data["isWliqi"] != null && (bool)data["isWliqi"]))
                {
                    currentPlayer.reached = true;
                    currentPlayer.safeTiles.Clear();
                    if (!syncing) InvokeOnReach(currentPlayer);
                }
                if (!syncing) InvokeOnDiscard(currentPlayer, tile);
                //JToken keyValuePairs = message.data;
                if (data["doras"] != null)
                {
                    var doras = data["doras"].Select(t => (string)t);
                    foreach (var dora in doras.Skip(gameData.dora.Count))
                    {
                        gameData.dora.Add(new Tile(dora));
                    }
                }
                if (data["operation"] != null)
                {
                    operationList = data["operation"]["operationList"];
                    if (!syncing)
                    {
                        stopwatch.Restart();
                        InvokeOnWait(tile, currentPlayer);
                    }
                }
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionChiPengGang")
            {
                var data = message.data["data"];
                var seat = 0;
                var type = 0;
                if (data["seat"] != null) seat = (int)data["seat"];
                if (data["type"] != null) type = (int)data["type"];
                Player currentPlayer = gameData.players[NormalizedPlayerId(seat)];
                var fuuro = HandleFuuro(currentPlayer, type, data["tiles"].Select(t => (string)t), data["froms"].Select(t => (int)t));

                if (!syncing) InvokeOnNaki(currentPlayer, fuuro);
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionAnGangAddGang")
            {
                var data = message.data["data"];
                var seat = 0;
                var type = 0;
                if (data["seat"] != null) seat = (int)data["seat"];
                if (data["type"] != null) type = (int)data["type"];
                Player currentPlayer = gameData.players[NormalizedPlayerId(seat)];
                FuuroGroup fuuro = null;
                if (type == 2)
                {
                    fuuro = HandleKakan(currentPlayer, (string)data["tiles"]);
                }
                else if (type == 3)
                {
                    fuuro = HandleAnkan(currentPlayer, (string)data["tiles"]);
                }

                Tile tile = fuuro.Last();
                if (!syncing) InvokeOnNaki(currentPlayer, fuuro);
                if (!syncing) InvokeOnChanKan(currentPlayer, tile);
            }
            else if (message.method == ".lq.ActionPrototype" && (string)message.data["name"] == "ActionBaBei")
            {
                var data = message.data["data"];
                var seat = 0;
                if (data["seat"] != null) seat = (int)data["seat"];
                Player currentPlayer = gameData.players[NormalizedPlayerId(seat)];
                Tile tile = HandleNuku(currentPlayer);
                if (!syncing) InvokeOnNuku(currentPlayer);
                if (!syncing) InvokeOnChanKan(currentPlayer, tile);
            }
        }

        private void HandleInit(JToken data)
        {
            int chang = 0, ju = 0, ben = 0, liqibang = 0;
            if (data["chang"] != null) chang = (int)data["chang"];
            if (data["ju"] != null) ju = (int)data["ju"];
            if (data["ben"] != null) ben = (int)data["ben"];
            if (data["liqibang"] != null) liqibang = (int)data["liqibang"];

            switch (chang)
            {
                case 0:
                    gameData.direction = Direction.E;
                    break;
                case 1:
                    gameData.direction = Direction.S;
                    break;
                case 2:
                    gameData.direction = Direction.W;
                    break;
            }

            gameData.seq = ju + 1;
            gameData.seq2 = ben;
            gameData.reachStickCount = liqibang;

            gameData.remainingTile = GameData.initialRemainingTile;

            gameData.dora.Clear();
            if (data["doras"] != null)
            {
                var doras = data["doras"].Select(t => (string)t);
                foreach (var dora in doras)
                {
                    gameData.dora.Add(new Tile(dora));
                }
            }

            int[] seats = data["scores"].Select(t => (int)t).ToArray();
            gameData.setPlayers(seats.Count());
            int playerCount = gameData.players.Count();
            for (int i = 0; i < playerCount; i++)
            {
                gameData.players[NormalizedPlayerId(i)].point = (int)data["scores"][i];
                gameData.players[NormalizedPlayerId(i)].reached = false;
                gameData.players[NormalizedPlayerId(i)].graveyard = new Graveyard();
                gameData.players[NormalizedPlayerId(i)].fuuro = new Fuuro();
                gameData.players[NormalizedPlayerId(i)].hand = new Hand();
            }

            int oyaNum = (playerCount - playerSeat + gameData.seq - 1) % playerCount;
            gameData.players[oyaNum].direction = Direction.E;
            gameData.players[(oyaNum + 1) % playerCount].direction = Direction.S;
            gameData.players[(oyaNum + 2) % playerCount].direction = Direction.W;
            if (playerCount == 4) gameData.players[(oyaNum + 3) % playerCount].direction = Direction.N;

            foreach (var tileName in data["tiles"].Select(t => (string)t))
            {
                player.hand.Add(new Tile(tileName));
            }
        }

        private FuuroGroup HandleFuuro(Player currentPlayer, int type, IEnumerable<string> tiles, IEnumerable<int> froms)
        {
            FuuroGroup fuuroGroup = new FuuroGroup();
            switch (type)
            {
                case 0:
                    fuuroGroup.type = FuuroType.chii;
                    break;
                case 1:
                    fuuroGroup.type = FuuroType.pon;
                    break;
                case 2:
                    fuuroGroup.type = FuuroType.minkan;
                    break;
            }

            foreach (var (tileName, from) in tiles.Zip(froms, Tuple.Create))
            {
                if (NormalizedPlayerId(from) != currentPlayer.id) // 从别人那里拿来的牌
                {
                    fuuroGroup.Add(gameData.lastTile);
                    gameData.lastTile.IsTakenAway = true;
                }
                else if (currentPlayer == player) // 自己的手牌
                {
                    Tile tile = player.hand.First(t => t.OfficialName == tileName);
                    player.hand.Remove(tile);
                    fuuroGroup.Add(tile);
                }
                else
                {
                    fuuroGroup.Add(new Tile(tileName));
                }
            }

            currentPlayer.fuuro.Add(fuuroGroup);
            return fuuroGroup;
        }

        private FuuroGroup HandleAnkan(Player currentPlayer, string tileName)
        {
            tileName = tileName.Replace('0', '5');

            FuuroGroup fuuroGroup = new FuuroGroup();
            fuuroGroup.type = FuuroType.ankan;

            if (currentPlayer == player)
            {
                IEnumerable<Tile> tiles = player.hand.Where(t => t.GeneralName == tileName).ToList();
                fuuroGroup.AddRange(tiles);
                player.hand.RemoveRange(tiles);
            }
            else
            {
                if (tileName[0] == '5' && tileName[1] != 'z') // 暗杠中有红牌
                {
                    fuuroGroup.Add(new Tile(tileName));
                    fuuroGroup.Add(new Tile(tileName));
                    fuuroGroup.Add(new Tile(tileName));
                    fuuroGroup.Add(new Tile("0" + tileName[1]));
                }
                else
                {
                    for (var i = 0; i < gameData.players.Count(); i++)
                    {
                        fuuroGroup.Add(new Tile(tileName));
                    }
                }
            }

            currentPlayer.fuuro.Add(fuuroGroup);
            return fuuroGroup;
        }

        private FuuroGroup HandleKakan(Player currentPlayer, string tileName)
        {
            FuuroGroup fuuroGroup = currentPlayer.fuuro.First(g => g.type == FuuroType.pon && g.All(t => t.GeneralName == tileName.Replace('0', '5')));
            fuuroGroup.type = FuuroType.kakan;

            if (currentPlayer == player)
            {
                Tile tile = player.hand.First(t => t.GeneralName == tileName.Replace('0', '5'));
                player.hand.Remove(tile);
                fuuroGroup.Add(tile);
            }
            else
            {
                fuuroGroup.Add(new Tile(tileName));
            }

            return fuuroGroup;
        }

        private Tile HandleNuku(Player currentPlayer)
        {
            if (currentPlayer == player)
            {
                Tile tile = player.hand.First(t => t.Name == "4z");
                player.hand.Remove(tile);
                player.nuku.Add(tile);
                return tile;
            }
            return new Tile(name: "4z");
        }

        public override void Pass()
        {
            Deley();
            Send(new { type = ReplyType.Pass }).Wait();
        }

        public override void Discard(Tile tile)
        {
            Deley(5);
            Send(new { type = nextReach ? ReplyType.Liqi : ReplyType.Discard, tile = tile.OfficialName, moqie = gameData.lastTile == tile, player.reached }).Wait();
            nextReach = false;
            lastDiscardedTile = tile;
        }

        public override void Chii(Tile tile0, Tile tile1)
        {
            Deley();
            var combination = operationList.First(item => (int)item["type"] == 2)["combination"].Select(t => (string)t);
            int index = combination.ToList().FindIndex(comb => comb.Split('|').OrderBy(t => t).SequenceEqual(new[] { tile0.OfficialName, tile1.OfficialName }.OrderBy(t => t)));
            Send(new { type = ReplyType.Chii, index, tile0 = tile0.OfficialName, tile1 = tile1.OfficialName, combination }).Wait();
        }

        public override void Pon(Tile tile0, Tile tile1)
        {
            Deley();
            var combination = operationList.First(item => (int)item["type"] == 3)["combination"].Select(t => (string)t);
            int index = combination.ToList().FindIndex(comb => comb.Contains(tile0.GeneralName));
            Send(new { type = ReplyType.Pon, index, tile0 = tile0.OfficialName, tile1 = tile1.OfficialName, combination }).Wait();
        }

        public override void Ankan(Tile tile)
        {
            Deley();
            var combination = operationList.First(item => (int)item["type"] == 4)["combination"].Select(t => (string)t);
            int index = combination.ToList().FindIndex(comb => comb.Contains(tile.GeneralName));
            Send(new { type = ReplyType.Ankan, index, tile = tile.OfficialName, combination }).Wait();
        }

        public override void Minkan()
        {
            Deley();
            Send(new { type = ReplyType.Minkan, index = 0 }).Wait();
        }

        public override void Kakan(Tile tile)
        {
            Deley();
            var combination = operationList.First(item => (int)item["type"] == 6)["combination"].Select(t => (string)t);
            int index = combination.ToList().FindIndex(comb => comb.Contains(tile.GeneralName) || comb.Contains(tile.OfficialName));
            Send(new { type = ReplyType.Kakan, index, tile = tile.OfficialName, combination }).Wait();
        }

        public override void Tsumo()
        {
            Deley();
            Send(new { type = ReplyType.Tsumo, index = 0 }).Wait();
        }

        public override void Ron()
        {
            Deley();
            Send(new { type = ReplyType.Ron, index = 0 }).Wait();
        }

        public override void Ryuukyoku()
        {
            Deley();
            Send(new { type = ReplyType.Ryuukyoku, index = 0 }).Wait();
        }

        public override void Nuku()
        {
            Deley();
            Send(new { type = ReplyType.Nuku, index = 0 }).Wait();
        }

        public override void Reach(Tile tile)
        {
            nextReach = true;
            player.reached = true;
        }

        // NOP
        public override void Login() { }
        public override void Join(GameType type) { }
        public override void EnterPrivateRoom(int roomNumber) { }
        public override void NextReady() { }
        public override void Bye() { }
        public override void Close(bool unexpected = false) { }

        class Message
        {
            public int id { get; set; } = -1;
            public MessageType type { get; set; }
            public string method { get; set; }
            public JToken data { get; set; }
        }

        enum MessageType
        {
            Notify = 1,
            Req = 2,
            Res = 3,
        }

        enum ReplyType
        {
            Pass = 0,
            Discard = 1,
            Chii = 2,
            Pon = 3,
            Ankan = 4,
            Minkan = 5,
            Kakan = 6,
            Liqi = 7,
            Tsumo = 8,
            Ron = 9,
            Ryuukyoku = 10,
            Nuku = 11,
        }
    }
}