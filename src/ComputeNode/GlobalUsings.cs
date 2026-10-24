global using System.Buffers;
global using System.Diagnostics;
global using System.IO.Compression;
global using System.Net.WebSockets;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using System.Text.Json.Serialization;
global using System.Threading.Channels;
// 领域层只暴露纯业务类型与持久化编解码；节点不得引用宿主、数据库或网络实现层。
// DaoHua.Ecs 是共享 ECS 执行环境：节点经同一套 Entity/System 运行时推进确定性补算。
global using DaoHua.Ecs;
global using Domain.Combat;
global using Domain.Common;
global using Domain.Economy;
global using Domain.GameData;
global using Domain.Persistence;
global using Domain.Player;
global using Domain.Simulation;
