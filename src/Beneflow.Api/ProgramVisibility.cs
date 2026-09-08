// 暴露顶层语句生成的 Program 类型，使集成测试可通过 WebApplicationFactory<Program> 启动真实 API 管线。
// 编译器会把本声明与生成的 internal partial Program 合并为一个 public 类型。
// 该文件不参与任何业务逻辑，仅用于测试可达性。
public partial class Program { }
