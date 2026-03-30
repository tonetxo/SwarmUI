#!/bin/bash

# Instalar slideshow para Nemo

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

echo -e "${YELLOW}Instalando Nemo Slideshow...${NC}"
echo ""

# Crear directorios si no existen
mkdir -p /home/tonetxo/.local/bin
mkdir -p /home/tonetxo/.local/share/nemo/actions

# 1. Verificar VLC
echo -e "${YELLOW}[1/4] Verificando VLC...${NC}"
if ! command -v vlc &> /dev/null; then
    echo -e "${RED}✗ VLC no está instalado${NC}"
    echo -e "${YELLOW}Instalando VLC...${NC}"
    sudo apt update
    sudo apt install -y vlc
    if [ $? -eq 0 ]; then
        echo -e "${GREEN}✓ VLC instalado${NC}"
    else
        echo -e "${RED}✗ Error instalando VLC${NC}"
        exit 1
    fi
else
    echo -e "${GREEN}✓ VLC ya está instalado${NC}"
fi

# 2. Copiar el script
echo ""
echo -e "${YELLOW}[2/4] Instalando script...${NC}"
cat > /home/tonetxo/.local/bin/nemo-slideshow.sh << 'EOFSCRIPT'
#!/bin/bash

# nemo-slideshow.sh
# Presentación de imágenes y vídeos (soporta archivos y carpetas)

if [ $# -eq 0 ]; then
    notify-send "Slideshow" "No se seleccionaron archivos o carpetas." -i dialog-warning
    exit 1
fi

archivos=()

for item in "$@"; do
    if [ ! -e "$item" ]; then
        notify-send "Slideshow" "No encontrado:\n$item" -i dialog-error
        exit 1
    fi

    if [ -d "$item" ]; then
        # Es una carpeta: añadir todos los archivos compatibles dentro
        while IFS= read -r -d '' file; do
            archivos+=("$file")
        done < <(find "$item" -type f \( -iname "*.jpg" -o -iname "*.jpeg" -o -iname "*.png" -o -iname "*.bmp" -o -iname "*.gif" -o -iname "*.webp" -o -iname "*.mp4" -o -iname "*.mkv" -o -iname "*.avi" -o -iname "*.mov" -o -iname "*.flv" -o -iname "*.webm" \) -print0 | sort -z)
    else
        # Es un archivo: añadir directamente
        archivos+=("$item")
    fi
done

# Verificar que haya al menos un archivo compatible
if [ ${#archivos[@]} -eq 0 ]; then
    notify-send "Slideshow" "No se encontraron imágenes o vídeos compatibles." -i dialog-warning
    exit 1
fi

# Lanzar VLC con todos los archivos encontrados
vlc --fullscreen --playlist-autostart --loop --image-duration=5 --video-on-top "${archivos[@]}" 2>/dev/null &

notify-send "Slideshow" "Presentación con ${#archivos[@]} archivos." -i video-display
EOFSCRIPT

chmod +x /home/tonetxo/.local/bin/nemo-slideshow.sh
echo -e "${GREEN}✓ Script instalado en ~/.local/bin/nemo-slideshow.sh${NC}"

# 3. Copiar la acción de Nemo
echo ""
echo -e "${YELLOW}[3/4] Instalando acción de Nemo...${NC}"
cat > /home/tonetxo/.local/share/nemo/actions/slideshow.nemo_action << 'EOFACTION'
[Nemo Action]
Name=Ver presentación (slideshow)
Comment=Mostrar imágenes y vídeos en una presentación con VLC
Exec=/home/tonetxo/.local/bin/nemo-slideshow.sh %F
Selection=any
Extensions=jpg;jpeg;png;bmp;gif;webp;mp4;mkv;avi;mov;flv;wmv;webm;nodedir
Quote=double
EOFACTION

echo -e "${GREEN}✓ Acción instalada en ~/.local/share/nemo/actions/slideshow.nemo_action${NC}"

# 4. Reiniciar Nemo
echo ""
echo -e "${YELLOW}[4/4] Reiniciando Nemo...${NC}"
nemo -q 2>/dev/null
sleep 1
echo -e "${GREEN}✓ Nemo reiniciado${NC}"

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  ✓ Slideshow Instalado${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo -e "${YELLOW}Cómo usar:${NC}"
echo "1. Abre Nemo (el gestor de archivos)"
echo "2. Selecciona imágenes, vídeos o una carpeta"
echo "3. Click derecho → 'Ver presentación (slideshow)'"
echo ""
echo -e "${YELLOW}Formatos soportados:${NC}"
echo "  Imágenes: jpg, jpeg, png, bmp, gif, webp"
echo "  Vídeos: mp4, mkv, avi, mov, flv, webm"
echo ""
echo -e "${YELLOW}Atajos en VLC:${NC}"
echo "  Espacio: Pausar/Reanudar"
echo "  F: Pantalla completa on/off"
echo "  Esc o Q: Salir"
echo "  N: Siguiente"
echo "  P: Anterior"
