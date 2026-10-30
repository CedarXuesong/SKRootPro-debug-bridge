ENTRY(_ZN5skpdb12gyro_payload9TransformEPNS0_5StateEjPNS0_5EventEmm)
SECTIONS {
  . = 0;
  .text : { *(.text.entry) *(.text*) }
  .rodata : ALIGN(16) { *(.rodata*) }
  /DISCARD/ : { *(.comment) *(.eh_frame*) *(.note*) }
}
