// ==============================================================================
// 文件名称: GlobalUsings.cs
// 主要职责: 领域测试层全局命名空间隐式引用声明。
// 核心逻辑 / 定义内容:
//   1. 隐式全局引用 Domain.Constants 常量模块。
//   2. 隐式全局引用 Domain 各子模块与 Xunit 测试框架。
// ==============================================================================

global using Domain.Constants;
global using Domain.GameData;
global using Domain.Market;
global using Domain.Models;
global using Domain.Simulation;
global using Simulation = Domain.Simulation.SimulationEngine;
global using Xunit;

