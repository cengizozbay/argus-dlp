# Argus — Ubuntu Sunucu Kurulumu (SIFIRDAN, adım adım)

Bu rehber Ubuntu'yu hiç bilmeyen biri içindir. Her komutun **ne işe yaradığını**,
**ne göreceğini** ve **takılırsan ne yapacağını** yazdım. Sırayla takip et, atlama.

> **Genel mantık:** ESXi'de bir Ubuntu makinesi (VM) açacağız → ona uzaktan (Windows'tan)
> bağlanacağız → üstüne .NET + PostgreSQL kurup Argus sunucusunu çalıştıracağız →
> Windows PC'lere agent'ı dağıtacağız. Sunucu Ubuntu'da, izlenen bilgisayarlar Windows.

---

## BÖLÜM 1 — ESXi'de Ubuntu VM oluştur

1. **Ubuntu Server ISO indir** (Windows'ta tarayıcıdan):
   https://ubuntu.com/download/server → **Ubuntu Server 24.04 LTS** → `.iso` dosyası.

2. **ISO'yu ESXi'ye yükle:** ESXi web arayüzü → **Storage** → **Datastore browser** → **Upload** → indirdiğin `.iso`.

3. **Yeni VM oluştur:** ESXi → **Create / Register VM** → *Create a new virtual machine* →
   - İsim: `argus-sunucu`
   - Guest OS family: **Linux**, Version: **Ubuntu Linux (64-bit)**
   - CPU: **2**, RAM: **4 GB**, Disk: **40 GB** (300 makineye kadar yeter)
   - CD/DVD Drive: **Datastore ISO file** → yüklediğin Ubuntu ISO'sunu seç, "Connect at power on" işaretli olsun.
   - Ağ (Network): şirket ağına bağlı olsun (VM Network).

4. **VM'i başlat** (Power on) ve **Console**'u aç (ekranını göreceksin).

---

## BÖLÜM 2 — Ubuntu'yu kur (VM konsolunda)

VM açılınca mavi kurulum ekranı gelir. Ok tuşları + Enter ile ilerle:

1. Dil: **English** (Türkçe seçilebilir ama komutlar İngilizce ekrana göre).
2. "Continue without updating" → devam.
3. Klavye: **Turkish** seçebilirsin.
4. Kurulum tipi: **Ubuntu Server** (normal).
5. Ağ: Otomatik (DHCP) IP alır. **Buradaki IP adresini bir yere not et** (örn. `192.168.1.50`) —
   sunucuya bu adresle bağlanacağız. (Sabit IP istersen ağ yöneticine sor.)
6. Proxy: boş bırak → Done.
7. Mirror: varsayılan → Done.
8. Disk: **Use an entire disk** → Done → Done → **Continue** (biçimlendirmeyi onayla).
9. **Profile setup** (ÖNEMLİ, bunları unutma):
   - Your name: `argus`
   - Server name: `argus-sunucu`
   - Username: **`argus`**  ← bu kullanıcı adını kullanacağız
   - Password: **güçlü bir şifre** (not et)
10. **"Install OpenSSH server" seçeneğini İŞARETLE** (SPACE tuşu) → bu olmadan uzaktan bağlanamayız!
11. Snaps: hiçbir şey seçme → Done.
12. Kurulum biter → **Reboot Now**. (ISO otomatik çıkar; çıkmazsa ESXi'de CD/DVD bağlantısını kaldır.)

Yeniden başlayınca siyah ekranda `argus-sunucu login:` yazar. Artık VM hazır.

---

## BÖLÜM 3 — Windows'tan sunucuya bağlan (SSH)

Artık VM konsoluna gerek yok; Windows bilgisayarından bağlanacağız.

1. Windows'ta **PowerShell** aç (Başlat → "PowerShell" yaz → Enter).
2. Bağlan (IP'yi 2. bölümde not ettiğin adresle değiştir):
   ```
   ssh argus@192.168.1.50
   ```
3. İlk seferde "Are you sure...? (yes/no)" → **yes** yaz Enter.
4. Şifreni gir (yazarken görünmez, normal). Bağlandın → komut satırı `argus@argus-sunucu:~$` olur.

> Bundan sonraki tüm komutlar **bu SSH penceresinde** çalışır. Kopyala-yapıştır yapabilirsin
> (PowerShell'e sağ tık = yapıştır).

---

## BÖLÜM 4 — Sistemi güncelle

```bash
sudo apt update && sudo apt upgrade -y
```
- `sudo` = yönetici olarak çalıştır (ilk kullanımda şifreni ister).
- `apt` = Ubuntu'nun program yükleyicisi. Bu komut sistemi günceller. Birkaç dakika sürer.

Temel araçlar:
```bash
sudo apt install -y git curl wget
```

---

## BÖLÜM 5 — .NET 8 kur (sunucuyu çalıştırmak için)

```bash
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/ms.deb
sudo dpkg -i /tmp/ms.deb
sudo apt update
sudo apt install -y dotnet-sdk-8.0
```
Kontrol:
```bash
dotnet --version
```
→ Ekranda `8.0.xxx` gibi bir sürüm görmelisin. Gördüysen .NET kuruldu. ✅

---

## BÖLÜM 6 — PostgreSQL (veritabanı) kur

```bash
sudo apt install -y postgresql
```
Veritabanını ve kullanıcıyı oluştur (**`SIFRE_BURAYA`** yerine güçlü bir şifre yaz, not et):
```bash
sudo -u postgres psql -c "CREATE DATABASE argus;"
sudo -u postgres psql -c "CREATE USER argus WITH PASSWORD 'SIFRE_BURAYA';"
sudo -u postgres psql -c "GRANT ALL PRIVILEGES ON DATABASE argus TO argus;"
sudo -u postgres psql -d argus -c "GRANT ALL ON SCHEMA public TO argus;"
```
Her komuttan sonra `CREATE DATABASE`, `CREATE ROLE`, `GRANT` gibi cevaplar görürsün = başarılı.

---

## BÖLÜM 7 — Argus kodunu getir ve derle

```bash
sudo mkdir -p /opt/argus
sudo chown argus /opt/argus
git clone <REPO_URL> /opt/argus/src
```
- `<REPO_URL>` = kodu yüklediğin repo adresi (örn. `https://github.com/kullanici/argus.git`).
- Repo özelse kullanıcı adı/şifre (veya token) ister.

Sunucuyu yayınla (derle):
```bash
cd /opt/argus/src
dotnet publish backend/Argus.Server.csproj -c Release -o /opt/argus/server
```
İlk sefer paketleri indirir, 1-3 dakika sürer. Sonunda `Argus.Server -> ...` +
hata yoksa bitti. Veri klasörü:
```bash
sudo mkdir -p /var/lib/argus
sudo chown argus /var/lib/argus
```

---

## BÖLÜM 8 — Sunucuyu servis yap (otomatik başlasın)

Aşağıdaki bloğu **olduğu gibi** yapıştır (PostgreSQL şifresini ve admin şifresini değiştir):
```bash
sudo tee /etc/systemd/system/argus.service > /dev/null <<'EOF'
[Unit]
Description=Argus Server
After=network.target postgresql.service

[Service]
WorkingDirectory=/opt/argus/server
ExecStart=/usr/bin/dotnet /opt/argus/server/Argus.Server.dll
Restart=always
RestartSec=5
User=argus
Environment=ARGUS_URLS=http://0.0.0.0:5099
Environment=ARGUS_PG=Host=localhost;Port=5432;Database=argus;Username=argus;Password=SIFRE_BURAYA
Environment=ARGUS_DATA=/var/lib/argus
Environment=ARGUS_ADMIN_PASS=ilk-admin-sifresi

[Install]
WantedBy=multi-user.target
EOF
```
Servisi başlat:
```bash
sudo systemctl daemon-reload
sudo systemctl enable --now argus
sudo systemctl status argus
```
→ `active (running)` (yeşil) görürsen sunucu çalışıyor. **`q`** ile çık.

**Test et:** Windows'ta tarayıcıdan `http://192.168.1.50:5099` aç → Argus giriş ekranı gelmeli.
Kullanıcı: **admin**, şifre: `ilk-admin-sifresi` (yukarıda yazdığın). İlk girişte şifre değiştirmeni ister.

> `ARGUS_PG` verildiği için otomatik **PostgreSQL** kullanır, tablolar kendiliğinden oluşur.

---

## BÖLÜM 9 — HTTPS (güvenli bağlantı — üretimde şart)

Agent'lar güvenli (https) bağlantı ister. En kolayı **Caddy**:
```bash
sudo apt install -y caddy
```
```bash
sudo tee /etc/caddy/Caddyfile > /dev/null <<'EOF'
argus.sirket.local {
    reverse_proxy localhost:5099
    tls internal
}
EOF
sudo systemctl restart caddy
```
- `argus.sirket.local` = sunucuya vereceğin isim (DNS'te bu ismi VM'in IP'sine yönlendir; ağ yöneticisi yapar).
- `tls internal` = iç ağ için Caddy kendi sertifikasını üretir. Bu durumda Caddy'nin kök
  sertifikasını GPO ile Windows'lara "güvenilir kök" olarak dağıtmalısın; yoksa agent'ı
  kurarken `-AllowInsecureTls` eklersin.
- **Gerçek alan adın + internete açık 443 portun varsa:** `tls internal` satırını sil →
  Caddy **ücretsiz gerçek sertifika** (Let's Encrypt) alır, hiçbir ek iş gerekmez.

Artık sunucu: `https://argus.sirket.local`

---

## BÖLÜM 10 — Güvenlik duvarı

```bash
sudo ufw allow 22/tcp
sudo ufw allow 443/tcp
sudo ufw allow 5099/tcp
sudo ufw --force enable
```
- 22 = SSH (senin bağlantın), 443 = HTTPS (Caddy), 5099 = sunucu portu.

---

## BÖLÜM 11 — Agent paketini ve eklentiyi sunucuya koy

**DİKKAT:** Agent ve tarayıcı eklentisi Ubuntu'da üretilemez (Windows'a özel).
Bunları **Windows bilgisayarında** bir kez üretip sunucuya kopyalarsın.

Windows'ta (Argus proje klasöründe, PowerShell):
```powershell
powershell -ExecutionPolicy Bypass -File deploy\build-agent-package.ps1
powershell -ExecutionPolicy Bypass -File deploy\build-extension.ps1
```
Çıkan dosyaları sunucuya gönder (yine Windows PowerShell'de):
```powershell
scp backend\wwwroot\download\argus-agent.zip  argus@192.168.1.50:/opt/argus/server/wwwroot/download/
scp backend\wwwroot\ext\*                      argus@192.168.1.50:/opt/argus/server/wwwroot/ext/
```
Artık panelin "Agent Yönetimi" ekranından paket indirilebilir ve eklenti dağıtılabilir.

---

## BÖLÜM 12 — Windows bilgisayarlara agent kur

1. Panelde **Firmalar** → firmanı seç → firma anahtarını (`tk_...`) kopyala.
2. Her Windows PC'de (veya GPO ile) **yönetici PowerShell**:
   ```
   install.ps1 -ServerUrl "https://argus.sirket.local" -TenantKey "tk_..."
   ```
   Agent gizli servis olarak kurulur, personel göremez/kapatamaz, .NET gerektirmez.
3. Tarayıcı eklentisi için: `install-extension-policy.ps1 -ServerUrl "https://argus.sirket.local"`
   içindeki ayarı GPO ile dağıt → Chrome/Edge otomatik kurar.

---

## BÖLÜM 13 — Günlük kullanım / bakım

**Sunucu loglarını izle:**
```bash
sudo journalctl -u argus -f     # (Ctrl+C ile çık)
```
**Sunucuyu yeniden başlat:**
```bash
sudo systemctl restart argus
```
**Kod güncellenince:**
```bash
cd /opt/argus/src && git pull
dotnet publish backend/Argus.Server.csproj -c Release -o /opt/argus/server
sudo systemctl restart argus
```
**Veritabanı yedeği (otomatik, her gece 02:00):**
```bash
(sudo crontab -l 2>/dev/null; echo "0 2 * * * pg_dump -U argus argus | gzip > /var/lib/argus/backup-\$(date +\%F).sql.gz") | sudo crontab -
```

---

## Sık karşılaşılan sorunlar

| Sorun | Çözüm |
|-------|-------|
| SSH bağlanamıyorum | VM açık mı? IP doğru mu? Kurulumda "OpenSSH server" işaretlendi mi? `ping 192.168.1.50` dene. |
| `sudo: command not found` / yetki | Komutun başına `sudo` koy; şifreni ister. |
| Panel açılmıyor (5099) | `sudo systemctl status argus` → `active` mi? Değilse `sudo journalctl -u argus -n 50` ile hataya bak. Firewall (Bölüm 10) açık mı? |
| `dotnet: command not found` | Bölüm 5'i tekrar yap; `dotnet --version` çalışmalı. |
| PostgreSQL şifre hatası | `argus.service` içindeki `Password=` ile Bölüm 6'da yazdığın şifre birebir aynı mı? |
| Agent bağlanmıyor | Sunucu adresi doğru mu? HTTPS sertifikası güvenilir değilse agent'ı `-AllowInsecureTls` ile kur. |

---

## Özet — sırayla ne yaptık
1. ESXi'de Ubuntu VM açtık, kurduk (SSH açık).
2. Windows'tan SSH ile bağlandık.
3. Sistemi güncelledik, .NET + PostgreSQL kurduk.
4. Kodu `git clone` ile getirip derledik.
5. systemd servisi yaptık (otomatik başlar).
6. Caddy ile HTTPS verdik, firewall açtık.
7. Agent/eklentiyi Windows'ta üretip sunucuya kopyaladık.
8. Windows PC'lere agent'ı dağıttık.
