// ==============================================================================
// 文件名称: GlobalUsings.cs
// 主要职责: 新基线服务端宿主、领域、协议和持久化命名空间隐式引用声明。
// ==============================================================================

global using System.Collections.Concurrent;
global using System.Buffers;
global using System.IO.Compression;
global using System.Net.WebSockets;
global using System.Security.Claims;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using System.Threading.Channels;
global using Microsoft.AspNetCore.Authentication;
global using Microsoft.AspNetCore.Authentication.Cookies;
global using Microsoft.AspNetCore.Authorization;
global using Microsoft.AspNetCore.Diagnostics;
global using Microsoft.AspNetCore.Diagnostics.HealthChecks;
global using Microsoft.AspNetCore.Mvc;
global using Microsoft.AspNetCore.Http.Features;
global using Microsoft.Extensions.Diagnostics.HealthChecks;
global using Microsoft.Extensions.Options;
global using System.Text.Encodings.Web;
global using Npgsql;
global using NpgsqlTypes;
global using Domain.Common;
global using Domain.Combat;
global using Domain.Economy;
global using Domain.GameData;
global using Domain.Market;
global using Domain.Persistence;
global using Domain.Player;
global using Domain.World;
