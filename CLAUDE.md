# NT8 Trade Copier + Risk Manager

## Projektin tavoite

Rakennetaan NinjaTrader 8:lle työkalu, joka:

1. Kopioi kaupat master-tililtä useille prop-firm-tileille (Apex, Tradeify)
2. Valvoo riskiä per tili ja pakottaa flatiksi kun rajat ylittyvät
3. Näyttää dashboardin, jossa kaikkien tilien tila näkyy yhdellä silmäyksellä

Käyttäjä on futuurikauppias (MYM, MES), kaupankäyntikehys on TDG "Prop Done Right".
Keskeiset käsitteet: DRB (Daily Risk Budget), R-multiple, trailing drawdown.

## Lähtötilanne

`SimpleTradeCopierV2.cs` on ilmainen, julkisesti jaettu NinjaTrader Ecosystem
-indikaattori. Sitä käytetään referenssinä ja lähtökohtana, ei sellaisenaan.

### Mitä siinä on hyvää — säilytä nämä ratkaisut

- ChartTrader-paneelin WPF-injektio (`FindFirst("ChartWindowChartTraderControl")`,
  oma `RowDefinition`, siisti dispose `State.Terminated`:ssä)
- Execution-pohjainen kopiointi (`Account.ExecutionUpdate`), EI order-pohjainen
- Deduplikointi `Execution.ExecutionId`:llä
- `Account.All` + `lock (Account.All)` tilien haussa

### Mitä siinä on rikki — nämä pitää korjata

1. **Stopit eivät kopioidu.** Kopioi vain executionit market-orderina. Follower-tilit
   jäävät ilman stoppia. Tämä on projektin tärkein korjattava asia.
2. **Rejection-käsittely puuttuu kokonaan.** `successfulOrders++` ajetaan heti
   `Submit()`:n jälkeen, vaikka submit on asynkroninen. `OnOrderUpdate` on tyhjä.
   Hylätty order lasketaan onnistuneeksi.
3. **Ei position-reconciliationia.** Jos yksi follower-order hylätään, tili jää
   pysyvästi eri positioon kuin master eikä kukaan huomaa.
4. **Yksi kerroin kaikille tileille.** Tarvitaan per-tili-kerroin ja per-tili-rajat.
5. **Indikaattori kuolee chartin mukana.** Riskienhallinta ei saa olla
   chart-sidonnainen.
6. `processedOrderIds`-siivous käyttää `HashSet.Take(500)` — HashSetin järjestys
   on määrittelemätön, joten se poistaa mielivaltaisia ID:itä.
7. Laskurit (`totalOrdersCopied` ym.) inkrementoidaan event-threadeista ilman
   lukkoa.

## Arkkitehtuuripäätös

Toteutetaan **NT8 AddOn** -tyyppinä (oma `NTWindow`), ei indikaattorina.

- Riskienhallinta ja kopiointi pyörivät AddOnissa, riippumatta siitä mikä chart
  on auki
- Chartin ChartTrader-nappi jää ohueksi kytkimeksi, joka vain vaihtaa AddOnin
  tilaa
- Tila jaetaan singleton-managerin kautta

Ehdotettu tiedostojako (Claude Code saa ehdottaa parempaa):

```
AddOns/
  CopierEngine.cs      // execution-kuuntelu, order-lähetys, reconciliation
  RiskManager.cs        // DRB, päivätappioraja, trailing DD, auto-flatten
  AccountState.cs        // per-tili tila: PnL, positio, rajat, kerroin
  CopierWindow.cs         // dashboard, NTWindow
  CopierSettings.cs       // asetusten serialisointi
Indicators/
  CopierChartButton.cs   // ohut ChartTrader-kytkin
```

## Vaatimukset

### Kopiointi

- Master → 1..N follower-tiliä
- Execution-pohjainen, deduplikoitu ExecutionId:llä
- **Stop-loss kopioidaan followereille natiivina stop-orderina**, ei vain
  positiona
- Per-tili quantity multiplier ja per-tili max contracts
- Partial fillit summautuvat oikein
- Rejection kuunnellaan `OrderUpdate`:sta (`OrderState.Rejected`) ja lokitetaan
  näkyvästi
- Reconciliation: jaksoittain (esim. 5 s) verrataan follower-positiot
  masteriin. Poikkeama → korjaava order tai hälytys. Konfiguroitava:
  auto-korjaus vai vain varoitus.

### Riskienhallinta (per tili)

- Päivittäinen tappioraja (DRB) — kovana rajana, ei suosituksena
- Trailing drawdown -seuranta Apexin ja Tradeifyn sääntöjen mukaan
- Max samanaikaiset kontraktit
- Raja täyttyy → flattaa kyseinen tili ja lukitse se päivän lopuksi (ei uusia
  kopioita)
- Kill switch: yksi nappi, joka flattaa KAIKKI tilit heti

### Dashboard

- Rivi per tili: nimi, yhteystila, positio, päivän realisoitu +
  realisoitumaton PnL, DRB käytetty / jäljellä, trailing DD -etäisyys, tilan
  väri (OK / varoitus / lukittu)
- Kopiointitilastot: lähetetyt, täytetyt, hylätyt
- Master-tilin tila erikseen ylhäällä
- Kill switch -nappi

## Työskentelytapa

- **Et voi kääntää etkä testata koodia.** NinjaScript kääntyy vain
  NinjaTrader 8:n NinjaScript Editorissa (F5). Käyttäjä kääntää ja liittää
  virheet takaisin.
- Kirjoita siksi varovaisesti ja pieninä paloina. Yksi tiedosto kerrallaan.
- Älä keksi NT8 API:a. Jos et ole varma metodin allekirjoituksesta, sano se
  suoraan ja kysy, mieluummin kuin arvaat.
- Kaikki testataan Sim-tileillä ennen livetilejä.
- Selitykset suomeksi, koodi ja kommentit englanniksi.

## Tärkeä muistutus

Tämä työkalu voi lähettää oikeita ordereita oikeille rahoitetuille tileille.
Virhe kopiointi- tai riskilogiikassa maksaa rahaa. Suosi yksinkertaista ja
varmasti toimivaa monimutkaisen ja fiksun sijaan. Jos jokin kohta on
epävarma, oletusarvo on: älä lähetä orderia, ja huuda lokiin.
