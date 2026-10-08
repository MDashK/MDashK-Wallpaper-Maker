# MDashK Wallpaper Maker Mobile

Versão para telemóvel (vertical) do MDashK Wallpaper Maker. É uma aplicação separada: tem executável, pasta `config`, histórico de aprendizagem e calibração próprios. Usa os mesmos modelos de IA e de waifu2x, que são copiados para junto do executável.

Para compilar, corre `E:\_MWPM\build_mobile.bat`. O resultado fica em `E:\_MWPM\Build Mobile` (executável + `onnxruntime.dll` + `DirectML.dll` + pasta `Models`).

## O que muda em relação ao desktop

O processamento é o do desktop rodado 90°:

| | Desktop | Mobile |
|---|---|---|
| Ajuste da imagem | à **altura** do ecrã | à **largura** do ecrã |
| Cópias espelhadas com blur | à esquerda e à direita | **em cima e em baixo** (alternando a inversão vertical) |
| Imagem maior do que o ecrã | corte horizontal | **corte vertical**, com o topo ancorado na cabeça |
| Imagem estreita e alta / baixa e larga | baixa e larga: imagem em cima + espelho em baixo | estreita e alta: imagem à esquerda + espelho à direita |
| Blur | 4 px a 1080 de altura | 4 px a 1080 de **largura** |
| Botão "Center" | um botão (horizontal) | dois botões: **Center ↕** (vertical) e **Center ↔** (horizontal) |

**Resoluções predefinidas:**

| Tipo | Resoluções |
|---|---|
| Android 20:9 / 19.5:9 / 18:9 / 16:9 | 1080x2400 (predefinida), 1080x2340, 1080x2160, 1080x1920 |
| iPhone | 1170x2532, 1179x2556, 1290x2796 |
| QHD+ | 1440x3200, 1440x3120 |
| HD+ | 720x1600 |

Também se pode escrever qualquer outra resolução.

Os resultados vão para `processed_output_<largura>x<altura>`, por exemplo `processed_output_1080x2400`.

## Enquadramento automático (IA)

Ainda não há wallpapers de telemóvel feitos à mão para calibrar, por isso os valores iniciais são estes:

- **Zoom:** a largura visível da imagem é 1,6 × a largura do meio-corpo da personagem, e o zoom é puxado 50% para x1,0. Na prática, os retratos aparecem inteiros.
- **Imagens em paisagem:** a imagem ocupa pelo menos 1/3 da altura do ecrã (no máximo uma cópia espelhada de cada lado). A IA faz zoom e corta as laterais, centrada na personagem.
- **Imagem mais alta do que o ecrã:** o topo do corte fica junto ao topo da cabeça (como no desktop).
- **Imagem mais baixa do que o ecrã:** fica centrada na vertical.
- **Resolução:** nunca amplia acima da resolução nativa (usa o waifu2x na pré-visualização se for preciso).

**Aprendizagem:** os ajustes manuais e as aprovações na pré-visualização ficam em `config\history.jsonl`, separados do desktop.
- A partir de 10 exemplos corrige a tendência: zoom, posição vertical e espaço acima da cabeça.
- A partir de 30 recalibra tudo.
- Se fizeres alguns wallpapers de telemóvel à mão (original + resultado), posso calibrar os valores iniciais com eles, como fiz para o desktop.

Tudo o resto funciona como no desktop:
- pré-visualização opcional com Center e barra verde dos ajustes manuais;
- IA ligada ou desligada por imagem;
- waifu2x (sempre disponível na pré-visualização) com GPU e escolha da placa;
- `not_processed` e limpeza da lista;
- pasta `config` portátil.
