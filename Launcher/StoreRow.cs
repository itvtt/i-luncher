namespace ILauncher;

/// <summary>스토어 탭의 한 줄 (앱스토어처럼 아이콘 · 이름 · 분류 · 설명 · 받기/열기 단추). 같은 앱 칸(AppCard)을 다른 모양으로 보여 줌</summary>
public sealed record StoreRow(AppCard Card);
