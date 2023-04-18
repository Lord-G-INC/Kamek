#ifndef __LIBKAMEK_H
#define __LIBKAMEK_H

enum patch_type {
	BIN,
	XML,
	INI,
	DOL,
};

struct ptr_info {
	/*
	 * Points to binary data (unsigned char *) for BIN
	 * and DOL, string array (char **) for XML and INI
	 */
	void *ptr;

	/*
	 * Size of data for BIN and DOL, number of
	 * strings in array for XML and INI
	 */
	size_t size;
};

union kamek_arg {
	char *str_val;
	int int_val;
};

extern void kamek_init(void);
extern struct ptr_info kamek_createpatch(const char **patches, int patch_count, union kamek_arg *patch_args, char *game, enum patch_type patch_type, unsigned int base_address);

#endif
