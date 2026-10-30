ENTRY(SkpdbTouchGuard)
SECTIONS {
  . = 0;
  .text : { *(.text.SkpdbTouchGuard) *(.text*) *(.rodata*) }
  /DISCARD/ : { *(.comment) *(.note*) *(.eh_frame*) }
}
