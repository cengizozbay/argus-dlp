# Argus — Dağıtım & Güncelleme Rehberi (Arhan)

Kod her değişince: **sunucuyu güncelle** + **agent'ları güncelle**. Aşağıdaki komutlar hazır,
sadece makine adlarını değiştir. Ortam bilgileri:

| Ne | Değer |
|---|---|
| Sunucu (panel/API) | `http://192.168.2.247:5099` (Ubuntu VM) |
| Firma anahtarı | `demo-tenant-key-001` |
| Domain | `arhangrup.local` |
| Fileserver | `filesrv` — paylaşım `\\filesrv\Arhan` = fiziksel `E:\Arhan` |
| Paket paylaşımı | `\\filesrv\Arhan\BT\argus-agent` |
| PsExec | `%USERPROFILE%\Desktop\PSTools\PsExec.exe` (DC'de) |

---

## 0. Yeni paketi al (kod değişince)
En güncel `argus-agent.zip`:
- **GitHub → repo → Actions** (build yeşil olsun) → **Releases → latest → argus-agent.zip**, VEYA
- AnyDesk'le geliştirme makinesinin masaüstündeki `aha` klasörü / `argus-agent.zip`.

Sonra **paylaşımdaki paketi güncelle** (bir kez): zip içeriğini `\\filesrv\Arhan\BT\argus-agent`'a
çıkar (üzerine yaz). Böylece agent exe + install script'leri güncellenir.

---

## 1. SUNUCUYU güncelle (Ubuntu SSH)
```bash
cd /opt/argus/src && git pull
dotnet publish backend/Argus.Server.csproj -c Release -o /opt/argus/server
sudo systemctl restart argus
```
Sonra panelde **Ctrl + Shift + R**.

---

## 2. UÇ NOKTA (client) agent kur/güncelle — ortaktan, kopyalama YOK

### Ön koşul: Domain Computers → Read (bir kez, KRİTİK)
psexec `-s` client'ta SYSTEM (makine hesabı) olarak çalışır; ortaktan okurken makine hesabı erişir.
`\\filesrv\Arhan\BT\argus-agent` (ya da üst klasör) → sağ tık → **Güvenlik** ve **Paylaşım** →
**`Domain Computers` : Read** ekle. (Bonus: bu izin **GPO'yu da açar**.)

### Her makineye tek komut (DC'de cmd)
```cmd
"%USERPROFILE%\Desktop\PSTools\PsExec.exe" \\MAKINEADI -s -h -accepteula powershell -ExecutionPolicy Bypass -File "\\filesrv\Arhan\BT\argus-agent\install.ps1" -ServerUrl "http://192.168.2.247:5099" -TenantKey "demo-tenant-key-001"
```
`MAKINEADI` yerine: `pinartokat`, `hazalseven`, ...
- **"Argus agent SERVICE kuruldu"** + `error code 0` → tamam.
- **"HATA: ...Argus.Agent.exe yok"** → Domain Computers read eksik (yukarıdaki ön koşul).

> `install.ps1` eski servisi **siler + yenisini kurar** → tamper/kilit/1056 derdi olmaz.
> Ortaktaki paket eski ise eski agent kurulur — **önce 0. adımı yap** (paylaşımı güncelle).

---

## 3. FİLESERVER denetim agent'ı (kim ne sildi/değiştirdi — gerçek ad)

Filesrv'de, **YÖNETİCİ PowerShell** (RDP/konsol) — ortaktaki güncel paketten:
```powershell
powershell -ExecutionPolicy Bypass -File "\\filesrv\Arhan\BT\argus-agent\install-fileserver.ps1" -ServerUrl "http://192.168.2.247:5099" -TenantKey "demo-tenant-key-001" -AuditFolders "E:\Arhan"
```
- **"File System > Basari"** + **"Argus FILESERVER agent kuruldu"** → tamam.
- `-AuditFolders`: denetlenecek **fiziksel** yollar (UNC değil), virgülle: `"E:\Arhan","E:\Ortak"`.

> Denetimi (audit policy + SACL) otomatik açar. Sadece **client değil**, fileserver'ın kendisine kurulur.

---

## 4. Tarayıcı eklentisi (gerçek URL / gizli mod)
`install.ps1` (uç nokta kurulumu) eklenti policy'sini + webhost köprüsünü **otomatik** kurar.
Kullanıcı tarayıcıyı bir kez **kapat-aç** → eklenti kendiliğinden yüklenir. Ekstra bir şey gerekmez.

---

## 5. Uzaktan agent kaldırma / yeniden başlatma
```cmd
:: Kaldir (panelden "Kaldir" da olur)
"%USERPROFILE%\Desktop\PSTools\PsExec.exe" \\MAKINEADI -s cmd /c "sc stop ArgusAgent & sc delete ArgusAgent"

:: Yeniden baslat (temiz)
"%USERPROFILE%\Desktop\PSTools\PsExec.exe" \\MAKINEADI -s powershell -c "Restart-Service ArgusAgent -Force"
```

---

## 6. Logları sıfırla (temiz test)
Ubuntu SSH:
```bash
sudo -u postgres psql -d argus -c "TRUNCATE events, alerts, web_usage, app_usage, doc_usage, summaries, agents;"
```
Firmalar/kullanıcılar/lisans durur. Agent'lar ~15-30 sn'de temiz olarak geri gelir.

---

## 7. Ne nerede yakalanır (özet)
| Sinyal | Nerede |
|---|---|
| Kim ne **sildi/değiştirdi** (ortakta) | **filesrv** audit agent — gerçek kullanıcı adıyla |
| **USB'ye kopya**, yerel dosya | Kişinin makinesindeki client agent |
| **Web / URL** (gizli mod dahil) | Client agent + tarayıcı eklentisi |
| **Belge süresi + konum** (hangi dosya, ne kadar, nerede) | Client agent (Belge sekmesi → satıra tıkla → konum/tam yol) |
| **USB engelleme** | Ayarlar → USB engelle (firma geneli) |

---

## 8. Sık sorun giderme
| Sorun | Çözüm |
|---|---|
| psexec "path not found" | PsExec yolunu doğrula: `dir "%USERPROFILE%\Desktop\PSTools\PsExec.exe"` |
| "Argus.Agent.exe yok" | Domain Computers → Read eksik (Adım 2 ön koşul) |
| Silme panele düşmüyor | filesrv agent eski olabilir → Adım 3 (güncel paketten) |
| Kullanıcı adı görünmüyor | Sunucu güncel değil → Adım 1 |
| Script "Parametre hatalı" / string terminator | Eski paket (encoding). Güncel paketi paylaşıma koy (Adım 0) |
| exe 1056 / kilit | `install.ps1` kullan (servisi siler); elle exe swap yapma |

---
*Son güncelleme akışı: paket → paylaşım → sunucu → client'lar → fileserver. Kod stabilse tek sefer, sonra sadece yeni makinelere Adım 2.*
