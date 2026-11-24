using System.Windows;

namespace IApi.Manager;

/// <summary>관리 창(ManagerView)이 함께 쓰는 것: 제목, 색 · 모양(ManagerResources.xaml), 업데이트 뒤 창 다시 열기</summary>
internal static class ManagerHost
{
    private static ResourceDictionary? _resources;

    /// <summary>머리 · 확인 창 제목</summary>
    internal const string Title = "i-api";

    internal static object Resource(string key) =>
        (_resources ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/i-api;component/Desktop/ManagerResources.xaml") })[key];

    /// <summary>업데이트한 뒤 이 창을 닫고 새 i-api.exe 의 창을 엶 (관리 창이 정함)</summary>
    internal static Action? RestartWindow { get; set; }
}
