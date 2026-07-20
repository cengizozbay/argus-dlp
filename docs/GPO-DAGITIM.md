# Argus — GPO ile Toplu Agent Dağıtımı (adım adım)

Domain'e (Active Directory) bağlı tüm Windows makinelere agent'ı **sessiz, otomatik** kurar.
Kullanıcı hiçbir şey görmez; agent gizli servis + tamper olarak açılışta yüklenir.

> **Ön koşul:** Bir Active Directory domain'in (Domain Controller) olacak ve hedef bilgisayarlar
> domain'e katılmış olacak. Domain yoksa GPO kullanılamaz (o durumda tek tek `install.ps1` ya da
> PDQ Deploy/SCCM gibi bir araç gerekir).

---

## Genel mantık
GPO bir **başlangıç scripti** (startup script) çalıştırır. Bu script açılışta **SYSTEM** (yönetici)
olarak koşar, ağ paylaşımındaki `install.ps1`'i çağırır, agent kurulur. SYSTEM olduğu için UAC/şifre
sormaz — toplu dağıtım için ideal.

---

## ADIM 1 — Agent dosyalarını ağ paylaşımına koy

En kolayı **NETLOGON** paylaşımı (tüm domain makineleri okuyabilir):

1. Domain Controller'da `argus-agent.zip`'i çıkar.
2. İçindeki `argus-agent` klasörünü şuraya kopyala:
   ```
   \\<DOMAIN>\NETLOGON\argus-agent\
   ```
   (Fiziksel yeri: DC'de `C:\Windows\SYSVOL\sysvol\<domain>\scripts\argus-agent\`)
3. İçinde `install.ps1`, `agent\`, `README.txt` görünmeli.

> **İzin (en kritik nokta!):** Başlangıç scripti bilgisayarın **makine hesabıyla** çalışır.
> Paylaşımda **Domain Computers** (veya Authenticated Users) **Read** iznine sahip olmalı.
> NETLOGON/SYSVOL bunu zaten sağlar; kendi paylaşımını kurarsan bu izni elle ver.

---

## ADIM 2 — Başlangıç scriptini hazırla

`deploy\gpo-deploy.ps1` dosyasını aç, üstteki **3 satırı** kendine göre ayarla:
```powershell
$Share     = "\\<DOMAIN>\NETLOGON\argus-agent"    # 1. adımdaki paylaşım
$ServerUrl = "http://192.168.2.247:5099"          # Argus sunucun
$TenantKey = "demo-tenant-key-001"                # firma anahtarı (panel > Firmalar)
```
Bu dosyayı da aynı paylaşıma (veya GPO script klasörüne) koy.

---

## ADIM 3 — GPO oluştur ve bağla

1. DC'de **Group Policy Management** aç.
2. Hedef **OU**'ya (bilgisayarların olduğu Organizational Unit) sağ tık →
   **Create a GPO in this domain, and Link it here…** → isim: `Argus Agent Dagitim`.
3. GPO'ya sağ tık → **Edit**.
4. Şu yolu aç:
   **Computer Configuration > Policies > Windows Settings > Scripts (Startup/Shutdown) > Startup**
5. **Startup** çift tık → **PowerShell Scripts** sekmesi → **Add** →
   `gpo-deploy.ps1`'in tam yolunu ver (örn. `\\<DOMAIN>\NETLOGON\argus-agent\gpo-deploy.ps1`).
6. Tamam / Uygula.

> **PowerShell Scripts** sekmesini kullan (Scripts değil) — script `.ps1` olduğu için.

---

## ADIM 4 — Uygula ve doğrula

- Hedef makineler **yeniden başlatılınca** kurulum çalışır (startup script açılışta koşar).
- Hızlı test için bir makinede: `gpupdate /force` → sonra **yeniden başlat**.
- Doğrulama:
  - Panelde **Agent Yönetimi** → makineler listeye düşer.
  - Makinede log: `C:\Windows\Temp\argus-deploy.log`
  - Servis: `sc query ArgusAgent` → RUNNING.

---

## Notlar
- **Zaten kurulu makineler atlanır** (script serviste varsa çıkar) → her açılışta tekrar kurmaz.
- **Kaldırma:** Panelden uzaktan "Agent'ı kaldır" ya da makinede `uninstall-service.ps1`.
  GPO'yu kaldırmak tek başına agent'ı silmez (kurulu servis kalır).
- **HTTPS:** Üretimde `$ServerUrl`'i `https://...` yap (Caddy ile sertifika). LAN testinde http yeter.
- **Firma bazlı:** Farklı firmalar/OU'lar için farklı `$TenantKey` ile ayrı GPO/klasör kullan.
- **Tarayıcı eklentisi:** Ayrıca Chrome/Edge force-install policy'sini de GPO ile dağıt
  (`deploy\install-extension-policy.ps1` içindeki HKLM anahtarları) → gerçek URL/gizli mod yakalanır.
```
