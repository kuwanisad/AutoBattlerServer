# 환경 설정 (ENVIRONMENT)

이 문서는 환경에 의존적인 설정, 수동 설치 항목, 경로 정보를 모아둔다.
새 환경에서 작업 시 반드시 이 문서를 먼저 확인한다.

---

## 프로젝트 구조

```
Main/
  GameProject1/
    DevTools/   ← 서버 실행 스크립트, Docker 설정
    Unity/      ← Unity 프로젝트 (클라이언트 + 인게임 서버)
    Server/     ← .NET 서버 프로젝트
```

---

## 시스템 요구사항

| 항목 | 버전 |
|------|------|
| .NET | 8.0 |
| Unity | 6.3 이상 |
| Git | 2.x 이상 |
| Docker Desktop | 최신 |

---

## Git PATH 설정

Unity Package Manager가 git URL로 패키지를 설치하려면
Git이 시스템 PATH에 등록되어 있어야 한다.

GitHub Desktop을 쓰는 경우 `cmd` 경로만 등록되어 있어서
Unity가 Git을 못 찾을 수 있다. 아래 명령으로 `bin` 경로를 추가한다.

```powershell
# 관리자 권한 PowerShell에서 실행
[System.Environment]::SetEnvironmentVariable(
    "Path",
    $env:Path + ";C:\Program Files\Git\bin",
    [System.EnvironmentVariableTarget]::Machine)
```

실행 후 Unity Hub와 Unity를 완전히 재시작해야 한다.

---

## Unity 프로젝트 환경

### 프로젝트 경로
```
{Main}/GameProject1/Unity/
```

### 설치된 패키지 (Package Manager)

| 패키지 | 버전 | 설치 방법 |
|--------|------|-----------|
| Netcode for GameObjects (NGO) | 2.11.0 | Unity Registry |
| YetAnotherHttpHandler | 1.8.0 | git URL |
| YetAnotherHttpHandler (Dependencies) | 0.1.0 | git URL |

**YetAnotherHttpHandler git URL 설치 방법:**

Package Manager → + → Add package from git URL

```
https://github.com/Cysharp/YetAnotherHttpHandler.git?path=src/YetAnotherHttpHandler#1.8.0
```

```
https://github.com/Cysharp/YetAnotherHttpHandler.git?path=src/YetAnotherHttpHandler.Dependencies#1.8.0
```

### 수동 설치 DLL (Assets/Plugins/)

아래 DLL들은 자동으로 설치되지 않으므로 수동으로 넣어야 한다.

| DLL | 출처 | 비고 |
|-----|------|------|
| Google.Protobuf.dll | NuGet: Google.Protobuf 3.25.1 (netstandard2.0) | protobuf 직렬화 |
| Grpc.Net.Client.dll | Grpc.Net.Client.Dependencies.unitypackage (netstandard2.1) | gRPC 클라이언트 |
| Grpc.Core.Api.dll | Grpc.Net.Client.Dependencies.unitypackage (netstandard2.1) | gRPC 코어 |
| Grpc.Net.Common.dll | Grpc.Net.Client.Dependencies.unitypackage (netstandard2.1) | gRPC 공통 |
| Microsoft.Extensions.Logging.Abstractions.dll | Grpc.Net.Client.Dependencies.unitypackage | 로깅 추상화 |
| System.Buffers.dll | Grpc.Net.Client.Dependencies.unitypackage | 버퍼 유틸리티 |
| System.Memory.dll | Grpc.Net.Client.Dependencies.unitypackage | 메모리 유틸리티 |

**Grpc.Net.Client.Dependencies.unitypackage 다운로드:**
```
https://github.com/Cysharp/YetAnotherHttpHandler/releases/tag/redist-20240111-01
```
다운로드 후 Unity에서 Assets → Import Package → Custom Package로 임포트.
"이미 모든 에셋이 있다"는 메시지가 뜨면 Library 폴더를 삭제하고 재시작 후 재시도.

**Google.Protobuf.dll 수동 설치:**
```powershell
$nugetUrl = "https://www.nuget.org/api/v2/package/Google.Protobuf/3.25.1"
Invoke-WebRequest -Uri $nugetUrl -OutFile "Google.Protobuf.nupkg"
Copy-Item "Google.Protobuf.nupkg" "Google.Protobuf.zip"
Expand-Archive "Google.Protobuf.zip" -DestinationPath "Google.Protobuf" -Force
Copy-Item "Google.Protobuf\lib\netstandard2.0\Google.Protobuf.dll" `
    "{Main}\GameProject1\Unity\Assets\Plugins\Google.Protobuf.dll"
```

### 네이티브 DLL (Assets/Plugins/x86_64/)

| DLL | 출처 |
|-----|------|
| librdkafka.dll 외 | NuGet: librdkafka.redist 2.6.0 |

```powershell
$unityNative = "{Main}\GameProject1\Unity\Assets\Plugins\x86_64"
$nativeSrc = "{사용자}\.nuget\packages\librdkafka.redist\2.6.0\runtimes\win-x64\native"
Copy-Item "$nativeSrc\*" $unityNative
```

---

## Contracts 빌드 → Unity 자동 복사

`Directory.Build.props`가 `Server/` 루트에 위치하며 Unity 경로를 상대 경로로 관리한다.
새 환경에서는 `Directory.Build.props`만 확인하면 된다.

```xml
<!-- Server/Directory.Build.props -->
<UnityProjectPath>$(MSBuildThisFileDirectory)..\Unity</UnityProjectPath>
<UnityServerGeneratedPath>$(UnityProjectPath)\Assets\Scripts\Network\Server\Generated</UnityServerGeneratedPath>
<UnityClientGeneratedPath>$(UnityProjectPath)\Assets\Scripts\Network\Client\Generated</UnityClientGeneratedPath>
```

| 프로젝트 | 생성 파일 | 복사 대상 |
|----------|-----------|-----------|
| IngameServer.Contracts | Ingame.cs, IngameGrpc.cs | Assets\Scripts\Network\Server\Generated\ |
| LobbyServer.Contracts | Lobby.cs, LobbyGrpc.cs | Assets\Scripts\Network\Client\Generated\ |
| ChatServer.Contracts | Chat.cs, ChatGrpc.cs | Assets\Scripts\Network\Client\Generated\ |

---

## Docker 컨테이너

`DevTools/docker-compose.yml`로 관리한다.
`start-servers.ps1` / `stop-servers.ps1`이 자동으로 처리한다.

| 서비스 | 이미지 | 포트 |
|--------|--------|------|
| kafka | confluentinc/cp-kafka:latest | 9092 |
| rabbitmq | rabbitmq:4-management | 5672, 15672 |
| redis | redis:7-alpine | 6379 |

RabbitMQ 관리 콘솔: `http://localhost:15672` (guest/guest)

---

## 서버 실행/종료

```powershell
cd "{Main}\GameProject1\DevTools"
Unblock-File .\start-servers.ps1
Unblock-File .\stop-servers.ps1
.\start-servers.ps1   # 시작
.\stop-servers.ps1    # 종료
```

스크립트가 `$PSScriptRoot` 기준으로 경로를 계산하므로
어느 환경에서도 폴더 구조만 맞으면 수정 없이 동작한다.

---

## shared.appsettings.json

Kafka/RabbitMQ/Redis 연결 정보를 담은 공유 설정 파일.
위치: `Server/GameServer/Shared/shared.appsettings.json`

각 서버 프로젝트 `csproj`에 빌드 시 출력 폴더로 자동 복사되는 설정이 있다.

**Redis 섹션은 마스터 서버만 직접 읽는다.** 로비/게임 에이전트/채팅/매칭 서버는 이
값을 로컬에서 읽지 않고, 마스터에 접속한 뒤 `SessionRegistryInfo` 메시지로 주소를
받아서 연결한다 (연결 전까지는 `ISessionRegistry` 호출이 대기한다). Kafka/RabbitMQ는
지금도 각 서버가 이 파일에서 직접 읽는다.

---

## 주의사항

- Unity Plugins 폴더의 DLL들은 Git에 포함되어 있지 않다.
  새 환경에서는 위 지침대로 수동 설치해야 한다.
- `System.Runtime` 버전 충돌 주의: .NET 8 기반 DLL을 Unity Plugins에 넣으면
  Unity Mono 런타임과 충돌한다. 반드시 `netstandard2.0` 또는 `netstandard2.1` 버전을 사용할 것.
- `Directory.Build.props`의 Unity 경로는 상대 경로이므로
  `GameProject1/Server/` 루트에 반드시 위치해야 한다.
