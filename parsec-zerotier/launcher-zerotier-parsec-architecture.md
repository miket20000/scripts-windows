# Architektura: Launcher + ZeroTier + Parsec Portable

## 1. Cel

Celem rozwiązania jest umożliwienie użytkownikowi połączenia z
przypisanym hostem Windows przez **Parsec Portable**, również z
problematycznych sieci, takich jak:

-   hotelowe Wi‑Fi,
-   hotspot iPhone,
-   sieci z CGNAT,
-   sieci z restrykcyjnym NAT/firewallem.

Użytkownik nie konfiguruje VPN ani parametrów sieciowych. Jego jedyne
czynności w Parsec to:

1.  uruchomienie launchera,
2.  zalogowanie się do konta Parsec,
3.  wybranie jedynego dostępnego hosta,
4.  kliknięcie **Connect**.

## 2. Architektura wysokiego poziomu

``` text
                     INTERNET
                         |
          +--------------+--------------+
          |                             |
          |      ZeroTier overlay       |
          |                             |
     +----v-----+                 +-----v-----+
     |   HOST   |<===============>|   GUEST   |
     | Windows  |   P2P / relay   |  Windows  |
     +----+-----+                 +-----+-----+
          |                             |
     ZeroTier                  launcher.exe (asInvoker)
     Parsec Host                        |
                                       +-- prywatny, ograniczony IPC
                                       |        |
                                       |        v
                                       |   helper.exe (UAC/admin)
                                       |        +-- operacje ZeroTier
                                       +-- uruchamia Parsec bez elevation
                                       |
                                       v
                                Parsec Portable
                                       |
                                użytkownik loguje się
                                       |
                                widzi jeden HOST
                                       |
                                    CONNECT
                                       |
                                       v
                                     HOST
```

## 3. Host

Każda maszyna HOST jest przygotowana wcześniej i posiada:

-   Windows,
-   Parsec Host,
-   ZeroTier,
-   członkostwo w odpowiedniej sieci ZeroTier,
-   konfigurację Parsec zapewniającą właściwemu kontu użytkownika dostęp
    do hosta.

Przykładowa adresacja:

``` text
HOST-17

LAN:       192.168.1.117
ZeroTier:  10.50.17.1
```

Host nie musi posiadać publicznego IP ani wystawiać klasycznego serwera
VPN.

## 4. Guest

Zakładamy możliwość uruchomienia na czystej maszynie Windows:

-   Parsec nie musi być zainstalowany,
-   użytkownik nie musi być wcześniej zalogowany do Parsec,
-   sieć może znajdować się za CGNAT lub restrykcyjnym NAT/firewallem,
-   użytkownik otrzymuje launcher przygotowujący środowisko.

Parsec działa w wersji **Portable**.

## 5. Launcher

Launcher jest warstwą orkiestrującą przygotowanie środowiska. Proces UI działa
jako zwykły użytkownik. Osobny helper z manifestem `requireAdministrator` jest
uruchamiany przez UAC i wykonuje wyłącznie zamknięty zestaw operacji ZeroTier
oraz przygotowanie ACL katalogu stanu. Kody aktywacyjne i tokeny `vm-manager`
pozostają w procesie użytkownika.

Docelowy przepływ:

``` text
launcher.exe
     |
     +-- 1. inicjalizacja
     +-- 2. przygotowanie/provisioning ZeroTier
     +-- 3. zestawienie sieci overlay
     +-- 4. weryfikacja osiągalności hosta
     +-- 5. przygotowanie Parsec Portable
     +-- 6. uruchomienie Parsec Portable
```

Launcher ma działać bez konieczności podawania przez użytkownika:

-   adresu IP hosta,
-   ZeroTier Network ID,
-   parametrów VPN,
-   danych uwierzytelniających ZeroTier.

Provisioning warstwy ZeroTier powinien być bezinterakcyjny.

## 6. ZeroTier

ZeroTier nie jest traktowany jako klasyczny model:

``` text
VPN Server <--- VPN Client
```

Host i guest są równorzędnymi peerami w sieci overlay:

``` text
HOST <=============> GUEST
        ZeroTier
```

Preferowana jest bezpośrednia, szyfrowana komunikacja P2P.

Jeżeli bezpośrednia ścieżka nie może zostać zestawiona z powodu
NAT/firewalla, ZeroTier może korzystać z komunikacji pośredniej/relay.

``` text
HOST <---- relay ----> GUEST
```

ZeroTier zapewnia zatem alternatywną ścieżkę sieciową w środowiskach, w
których natywna negocjacja P2P Parsec może nie działać.

## 7. Routing

ZeroTier nie powinien działać jako full-tunnel VPN dla całego ruchu
guesta.

Docelowo:

``` text
                  GUEST
                    |
          +---------+---------+
          |                   |
    zwykły Internet      ruch do HOST
          |                   |
        Wi-Fi              ZeroTier
          |                   |
          v                   v
      Internet              HOST
```

Normalny ruch internetowy pozostaje na fizycznym interfejsie
Wi‑Fi/Ethernet/hotspot.

ZeroTier zapewnia prywatną ścieżkę do hosta/sieci overlay. Nie należy
konfigurować ZeroTier jako domyślnej bramy `0.0.0.0/0`.

Dostęp guest → host powinien być dodatkowo ograniczony politykami
ZeroTier i/lub Windows Firewall, aby guest miał wyłącznie niezbędny
dostęp.

## 8. Parsec Portable

Po przygotowaniu connectivity launcher uruchamia Parsec Portable.

Launcher:

-   nie przechowuje loginu ani hasła Parsec,
-   nie wykonuje automatycznego logowania użytkownika,
-   nie musi automatycznie wybierać hosta.

Proces użytkownika:

``` text
Parsec Portable
       |
       v
     LOGIN
       |
       v
konto użytkownika
       |
       v
   jeden HOST
       |
       v
    CONNECT
```

Przyjmujemy zasadę:

> Każde konto Parsec używane przez guesta ma dostęp dokładnie do jednego
> hosta.

## 9. Podział odpowiedzialności

  Warstwa    Odpowiedzialność
  ---------- -----------------------------------------------------
  Launcher   API backendu, preflight, telemetria i start Parsec bez elevation
  Helper     podwyższona instalacja i ograniczone operacje CLI ZeroTier
  ZeroTier   connectivity guest ↔ host
  Parsec     uwierzytelnienie użytkownika i sesja remote desktop

## 10. Docelowy UX

Z perspektywy użytkownika:

``` text
1. Pobierz launcher.exe

2. Uruchom launcher.exe

        "Przygotowywanie połączenia..."

3. Launcher automatycznie:
        przygotowuje ZeroTier
        |
        v
        zestawia connectivity z HOST
        |
        v
        uruchamia Parsec Portable

4. Użytkownik loguje się do Parsec

5. Użytkownik widzi jeden host

6. Użytkownik klika CONNECT

7. Rozpoczyna się sesja z HOST
```

Użytkownik nie konfiguruje ZeroTier, VPN, routingu ani adresów IP.

## 11. Izolacja hostów

Jeżeli system obsługuje wiele par guest/host, należy zapewnić izolację
na poziomie sieciowym.

Logiczny model:

``` text
Guest A -------- Host A
Guest B -------- Host B
Guest C -------- Host C
```

Nawet jeżeli urządzenia należą do wspólnej infrastruktury ZeroTier,
polityki sieciowe powinny uniemożliwiać guestowi komunikację z hostami,
do których nie został przypisany.

Ograniczenie widoczności hosta w Parsec jest dodatkową warstwą
aplikacyjną, a nie zamiennikiem izolacji sieciowej.

## 12. Granica uprawnień

Dystrybucja składa się z dwóch podpisanych plików EXE. Launcher tworzy losowy
named pipe dostępny wyłącznie dla administratora i uruchamia dokładny helper
przez UAC. Obie strony porównują PID procesu połączonego z pipe, a helper
akceptuje wyłącznie jawne operacje: przygotowanie ACL stanu, wykrycie/instalację
przypiętej wersji ZeroTier, odczyt wersji i Node ID, `join`, `leave` oraz
oczekiwanie na gotowość dokładnej sieci. Protokół nie obsługuje dowolnego
polecenia, ścieżki ani argumentów procesu.

Proces UI wykonuje odczyty tras, komunikację z `vm-manager`, dostęp do stanu
DPAPI po przygotowaniu ACL, telemetrię i obsługę Parsec. Jeżeli UI zostanie ręcznie uruchomione
z podwyższonym tokenem, odmawia kontynuacji. Dzięki temu Parsec zawsze dziedziczy
zwykły token procesu interaktywnego.
